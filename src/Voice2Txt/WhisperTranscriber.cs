using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Voice2Txt.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Voice2Txt;

/// <summary>
/// whisper.cpp(Whisper.net)をプロセス内に取り込む。日本語固定。バックエンドは設定 backend の順(auto: CUDA → Vulkan → CPU)に試す。
/// 語の時刻(DTW)を出せるモデルでは、確定版の文字が無い声の区間を拾い直す(<see cref="Recovery"/>)。
/// </summary>
internal sealed class WhisperTranscriber : IEngine, ISpanDecoder
{
    private readonly WhisperFactory _factory;
    private readonly ProcessorPool _processor;
    private readonly ProcessorPool? _recover;
    private readonly bool _fit; // 速さを優先する(窓を縮める)
    private readonly StringBuilder _decoded = new(); // この発話でここまでに確定版の復号が返した文字列(縮めた窓の繰り返しを見つける。_one の中だけ)
    private readonly double _silenceThreshold;
    private readonly SemaphoreSlim _one = new(1, 1);
    // 途中経過で拾い直した区間の結果。確定版で同じ音の区間なら復号し直さない(離してから届くまでに 2 回目の復号を足さない)
    private readonly SpanCache _spans = new();

    public string Runtime { get; }

    // カスタム語彙の寄せ(語彙が無い・whisper.cpp に掛けられなければ null)。_one の中だけで使う
    private readonly KeywordHook? _keywords;

    private WhisperTranscriber(WhisperFactory f, ProcessorPool p, ProcessorPool? recover, double silenceThreshold, string runtime, KeywordHook? keywords)
    {
        _factory = f; _processor = p; _recover = recover; _silenceThreshold = silenceThreshold; Runtime = runtime; _fit = p.Fit; _keywords = keywords;
    }

    /// <summary>
    /// モデルを読み込む。<paramref name="modelName"/> の語の時刻の当て方(alignment heads)が分かれば DTW を有効にして拾い直しを使う。
    /// DTW を有効にした whisper.cpp は窓を送る途中で返し終えることがある(30 秒前後より長い入力で後ろが黙って消える)ので、
    /// <see cref="Recovery.TranscribeAsync"/> が最後の区切りの終わりから続きを渡し直す。
    /// </summary>
    /// <param name="preferSpeed">設定 preferSpeed。真なら復号ごとに、渡す音声の長さに縮めた窓(<see cref="Decoding.AudioContext"/>)の処理器を使う。</param>
    /// <param name="keywords">設定 keywords(カスタム語彙)。表記を初期プロンプトに足し(<see cref="Decoding.PromptFor"/>)、復号を表記へ寄せる(<see cref="KeywordBias"/>)。</param>
    public static WhisperTranscriber Load(string modelPath, string? modelName = null, double silenceThreshold = 0.01, string? backend = null, bool preferSpeed = false,
        IEnumerable<string>? keywords = null)
    {
        var prompt = Decoding.PromptFor(keywords);
        var bias = new KeywordBias(keywords);
        var hook = bias.IsEmpty ? null : new KeywordHook(bias);
        // バックエンドは設定 backend の順に読めるものを使う(auto: CUDA → Vulkan → CPU)。プロセスで最初に読めたものが使われ続ける
        RuntimeOptions.RuntimeLibraryOrder = Backends.Order(backend).Select(n => Enum.Parse<RuntimeLibrary>(n)).ToList();
        Recovery.Diag ??= AppLog.Write;
        var heads = AlignmentHeads(modelName);
        var f = heads is { } h
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = h })
            : WhisperFactory.FromPath(modelPath);
        var p = new ProcessorPool(ctx =>
        {
            var proc = WithContext(f.CreateBuilder().WithLanguage(Decoding.Language).WithPrompt(prompt).WithTokenTimestamps(), ctx).Build();
            if (hook is not null && !hook.Install(proc)) hook.Disable();
            return proc;
        }, preferSpeed);
        var r = heads is null ? null : new ProcessorPool(ctx => WithContext(f.CreateBuilder().WithLanguage("auto").WithNoContext(), ctx).Build(), preferSpeed);
        var rt = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        AppLog.Write($"keyword-bias words={bias.Words.Count} hooked={hook?.Enabled ?? false}"); // 語彙の本文は書かない
        return new WhisperTranscriber(f, p, r, silenceThreshold, rt, hook?.Enabled == true ? hook : null);
    }

    // 縮めた窓では温度を上げてやり直さない(繰り返しで失敗とされた復号を、5 通りの標本で何度もやり直して遅くなる。繰り返しは DecodeGuardedAsync が落とす)
    private static WhisperProcessorBuilder WithContext(WhisperProcessorBuilder b, int ctx) => ctx > 0 ? b.WithAudioContextSize(ctx).WithTemperatureInc(0) : b;

    /// <summary>モデルの名前から DTW の alignment heads(分からなければ null = 拾い直さない)。</summary>
    internal static WhisperAlignmentHeadsPreset? AlignmentHeads(string? modelName) => modelName?.ToLowerInvariant() switch
    {
        "tiny" => WhisperAlignmentHeadsPreset.Tiny,
        "base" => WhisperAlignmentHeadsPreset.Base,
        "small" => WhisperAlignmentHeadsPreset.Small,
        "medium" => WhisperAlignmentHeadsPreset.Medium,
        "large-v3" => WhisperAlignmentHeadsPreset.LargeV3,
        "large-v3-turbo" => WhisperAlignmentHeadsPreset.LargeV3Turbo,
        _ => null,
    };

    public Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct) => RunAsync(samples16k, false, ct);

    /// <summary>途中経過: 英字に読めた区間は先に拾い直して覚えておく(確定版がその区間を飛ばしても、離した後に 2 回目の復号を回さない)。</summary>
    public Task<string> TranscribeInterimAsync(float[] samples16k, CancellationToken ct) => RunAsync(samples16k, true, ct);

    private async Task<string> RunAsync(float[] samples16k, bool prefetch, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            _decoded.Clear();
            if (_recover is null)
            {
                var sb = new StringBuilder();
                foreach (var seg in await DecodeMainAsync(samples16k, _fit ? _decoded : null, ct)) sb.Append(seg.Text);
                return sb.ToString().Trim();
            }
            var r = await Recovery.TranscribeAsync(samples16k, _silenceThreshold, this, ct, _spans, prefetch);
            if (r.Recovered > 0) AppLog.Write($"recovered spans={r.Recovered} reused={r.Reused} holes={r.Holes}"); // 本文は書かない
            return r.Text;
        }
        finally { _one.Release(); }
    }

    // ISpanDecoder: TranscribeAsync が _one を持ったまま呼ぶ(外から直接は呼ばない)
    async Task<IReadOnlyList<DecodedSegment>> ISpanDecoder.DecodeAsync(float[] samples, CancellationToken ct)
    {
        var res = new List<DecodedSegment>();
        foreach (var seg in await DecodeMainAsync(samples, _fit ? _decoded : null, ct))
            res.Add(new DecodedSegment(seg.Text, seg.Tokens, seg.EndSeconds));
        return res;
    }

    /// <summary>
    /// 確定版・途中経過の復号(初期プロンプトの処理器)。カスタム語彙があれば語彙の表記へ寄せて復号し、寄せたために選ぶ語が変わったときだけ
    /// 同じ音声を寄せずに復号し直して、どちらを採るかを <see cref="KeywordBias.Accept"/> で決める(変わらなければ復号は 1 回のまま)。
    /// <paramref name="guard"/>(縮めた窓の繰り返しの見張り)には、採った方の文字列だけを足す。
    /// </summary>
    private async Task<List<Seg>> DecodeMainAsync(float[] samples, StringBuilder? guard, CancellationToken ct)
    {
        var p = _processor.For(samples);
        var kw = _keywords;
        if (kw is null || !kw.Enabled || !kw.Bias.HasVocabulary)
        {
            kw?.Bias.Begin(on: false);
            return await DecodeGuardedAsync(p, samples, guard, ct);
        }
        int mark = guard?.Length ?? 0;
        kw.Bias.Begin(on: true);
        var biased = await DecodeGuardedAsync(p, samples, guard, ct);
        var b = kw.Bias.Result;
        if (!b.Flipped) return biased;
        if (guard is not null) guard.Length = mark;
        kw.Bias.Begin(on: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plain = await DecodeGuardedAsync(p, samples, guard, ct);
        var a = kw.Bias.Result;
        bool take = KeywordBias.Accept(string.Concat(plain.Select(s => s.Text)), string.Concat(biased.Select(s => s.Text)), a, b, kw.Bias.Words);
        // 本文・語彙は書かない
        AppLog.Write($"keyword-bias flipped=1 taken={(take ? 1 : 0)} rest={b.Score - a.Score + b.Gap:F2} unreliable={(a.Unreliable || b.Unreliable ? 1 : 0)} ms={sw.ElapsedMilliseconds}");
        if (!take) return plain;
        if (guard is not null) { guard.Length = mark; foreach (var s in biased) guard.Append(s.Text); }
        return biased;
    }

    Task<(string Text, double NoSpeech)> ISpanDecoder.DecodeSpanAsync(float[] samples, CancellationToken ct) => RecoverAsync(_recover!.For(samples), samples, "span-decode", _fit ? new StringBuilder() : null, ct);

    // 語の穴は日本語の発話の中なので、拾い直しの processor を日本語に固定して使う(言語の判定の符号化を省く)。終われば自動判定に戻す
    async Task<(string Text, double NoSpeech)> ISpanDecoder.DecodeHoleAsync(float[] samples, CancellationToken ct)
    {
        var p = _recover!.For(samples);
        p.ChangeLanguage(Decoding.Language);
        try { return await RecoverAsync(p, samples, "hole-decode", _fit ? new StringBuilder() : null, ct); }
        finally { p.ChangeLanguage("auto"); }
    }

    private static async Task<(string Text, double NoSpeech)> RecoverAsync(WhisperProcessor p, float[] samples, string label, StringBuilder? guard, CancellationToken ct)
    {
        var sb = new StringBuilder();
        double noSpeech = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var seg in await DecodeGuardedAsync(p, samples, guard, ct)) { sb.Append(seg.Text); noSpeech = Math.Max(noSpeech, seg.NoSpeechProbability); }
        AppLog.Write($"{label} ms={sw.ElapsedMilliseconds} sec={samples.Length / (double)Audio.SampleRate:F1}"); // 本文は書かない
        return (sb.ToString(), noSpeech);
    }

    /// <summary>
    /// 復号の区切りを集める。<paramref name="guard"/>(窓を縮めたとき。それまでの文字列で、返した区切りを足していく)があれば
    /// 崩れた区切り(<see cref="Decoding.Derailed"/>)から後ろを捨て、復号を止める。
    /// 止めても whisper.cpp が返し終えるまで待つ(同じ処理器を次の復号に使うため)。
    /// </summary>
    private static async Task<List<Seg>> DecodeGuardedAsync(WhisperProcessor p, float[] samples, StringBuilder? guard, CancellationToken ct)
    {
        var res = new List<Seg>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool derailed = false;
        double sec = samples.Length / (double)Audio.SampleRate;
        try
        {
            await foreach (var seg in p.ProcessAsync(samples, stop.Token))
            {
                if (derailed) continue;
                if (guard is not null && Decoding.Derailed(sec, guard.ToString(), seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text))
                {
                    derailed = true;
                    AppLog.Write($"decode-derailed at={seg.Start.TotalSeconds:F2} end={seg.End.TotalSeconds:F2} len={sec:F2} kept={res.Count}"); // 本文は書かない
                    stop.Cancel();
                    continue;
                }
                var tokens = seg.Tokens.Select(t => new TimedToken(t.DtwTimestamp < 0 ? -1 : t.DtwTimestamp / 100.0, t.Text ?? "")).ToList();
                var text = seg.Text;
                if (guard is not null && Decoding.TrimRepeatedTail(text) is var kept && kept.Length < text.Length)
                {
                    // 1 つの区切りの中の繰り返し: 繰り返した分の文字と語を落とす
                    AppLog.Write($"decode-repeat at={seg.Start.TotalSeconds:F2} chars={text.Length}->{kept.Length}"); // 本文は書かない
                    var at = Recovery.Align(text, tokens);
                    tokens = tokens.Where((_, i) => at[i] < kept.Length).ToList();
                    text = kept;
                }
                res.Add(new Seg(text, tokens, seg.End.TotalSeconds, seg.NoSpeechProbability));
                guard?.Append(text);
            }
        }
        catch (Exception) when (derailed && !ct.IsCancellationRequested) { }
        return res;
    }

    /// <summary>復号の区切り(繰り返しを落とした後)。</summary>
    private sealed record Seg(string Text, IReadOnlyList<TimedToken> Tokens, double EndSeconds, double NoSpeechProbability);

    public void Dispose()
    {
        _recover?.Dispose();
        _processor.Dispose();
        _factory.Dispose();
        _keywords?.Dispose();
    }

    /// <summary>
    /// <see cref="KeywordBias"/> を whisper.cpp の復号の 1 手(logits filter callback)に掛ける。Whisper.net はこの入口を公開していないので、
    /// 処理器が復号ごとに whisper.cpp へ渡す設定(非公開のフィールド whisperParams)に、関数と状態の手がかりを書き込む(Whisper.net 1.9.1 の形)。
    /// 形が違って書けない・語の一覧を読めないときは寄せない(初期プロンプトの語彙だけが効く。ログ keyword-bias hooked=False)。
    /// 語の一覧は最初の復号(暖機)で whisper.cpp の文脈から読む。
    /// </summary>
    private sealed unsafe class KeywordHook : IDisposable
    {
        private static readonly System.Reflection.FieldInfo? ParamsField =
            typeof(WhisperProcessor).GetField("whisperParams", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // whisper.h の whisper_token_data(id, tid, p, plog, pt, ptsum, t0, t1, t_dtw, vlen)の大きさ。語の番号は先頭
        private const int TokenDataSize = 56;
        private GCHandle _self;
        private int[] _ids = new int[256];
        private int _nVocab;

        public KeywordBias Bias { get; }
        public bool Enabled { get; private set; } = true;

        public KeywordHook(KeywordBias bias) { Bias = bias; _self = GCHandle.Alloc(this); }

        public void Disable() => Enabled = false;

        public bool Install(WhisperProcessor p)
        {
            try
            {
                if (ParamsField?.GetValue(p) is not { } boxed) return false;
                var t = boxed.GetType();
                var cb = t.GetField("LogitsFilterCallback");
                var data = t.GetField("LogitsFilterCallbackData");
                if (cb is null || data is null || cb.FieldType != typeof(IntPtr) || data.FieldType != typeof(IntPtr)) return false;
                cb.SetValue(boxed, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, float*, IntPtr, void>)&OnLogits);
                data.SetValue(boxed, GCHandle.ToIntPtr(_self));
                ParamsField.SetValue(p, boxed);
                return true;
            }
            catch (Exception e)
            {
                AppLog.Write($"keyword-bias install-failed {e.GetType().Name}");
                return false;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnLogits(IntPtr ctx, IntPtr state, IntPtr tokens, int n, float* logits, IntPtr user)
        {
            // whisper.cpp の復号の中から呼ばれる。例外を外へ出さない
            try
            {
                if (user == IntPtr.Zero || GCHandle.FromIntPtr(user).Target is not KeywordHook h || !h.Enabled) return;
                h.Step(ctx, tokens, n, logits);
            }
            catch (Exception e)
            {
                try { if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is KeywordHook h) h.Enabled = false; } catch { }
                AppLog.Write($"keyword-bias callback-failed {e.GetType().Name}");
            }
        }

        private void Step(IntPtr ctx, IntPtr tokens, int n, float* logits)
        {
            if (!Bias.HasVocabulary && !LoadVocabulary(ctx)) { Enabled = false; return; }
            int nv = _nVocab;
            if (n < 0 || n > 4096) return;
            if (_ids.Length < n) _ids = new int[Math.Max(n, _ids.Length * 2)];
            for (int i = 0; i < n; i++) _ids[i] = *(int*)((byte*)tokens + (long)i * TokenDataSize);
            Bias.OnLogits(_ids.AsSpan(0, n), new Span<float>(logits, nv));
        }

        private bool LoadVocabulary(IntPtr ctx)
        {
            var lib = Native.GetModuleHandle("whisper.dll");
            if (lib == 0 || !NativeLibrary.TryGetExport(lib, "whisper_n_vocab", out var nVocab) || !NativeLibrary.TryGetExport(lib, "whisper_token_to_str", out var toStr)
                || !NativeLibrary.TryGetExport(lib, "whisper_token_eot", out var eotFn))
            {
                AppLog.Write("keyword-bias vocab-unavailable");
                return false;
            }
            int nv = ((delegate* unmanaged[Cdecl]<IntPtr, int>)nVocab)(ctx);
            int eot = ((delegate* unmanaged[Cdecl]<IntPtr, int>)eotFn)(ctx);
            if (nv <= 0 || eot <= 0 || eot >= nv) return false;
            var list = new byte[nv][];
            for (int i = 0; i < nv; i++)
            {
                var s = (byte*)((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)toStr)(ctx, i);
                int len = 0;
                if (s != null) while (s[len] != 0) len++;
                list[i] = s == null ? [] : new ReadOnlySpan<byte>(s, len).ToArray();
            }
            _nVocab = nv;
            Bias.SetVocabulary(list, eot);
            AppLog.Write($"keyword-bias vocab={nv}");
            return true;
        }

        public void Dispose()
        {
            Enabled = false;
            if (_self.IsAllocated) _self.Free();
        }
    }

    /// <summary>
    /// 同じ設定の処理器を窓(audio_ctx)ごとに持つ。速さを優先しないなら 30 秒の窓の 1 つだけ。優先するなら渡す音声の長さで窓を選び
    /// (<see cref="Decoding.AudioContext"/>)、初めての窓なら作って覚えておく(窓は 2 秒刻みで高々 10 種類。使われた窓だけ作る)。
    /// 呼び出しは <see cref="_one"/> の中だけ(スレッドセーフでなくてよい)。
    /// </summary>
    private sealed class ProcessorPool(Func<int, WhisperProcessor> build, bool fit) : IDisposable
    {
        private readonly Dictionary<int, WhisperProcessor> _byContext = new() { [0] = build(0) };

        public bool Fit => fit;

        public WhisperProcessor For(float[] samples)
        {
            int ctx = fit ? Decoding.AudioContext(samples.Length) : 0;
            if (!_byContext.TryGetValue(ctx, out var p))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _byContext[ctx] = p = build(ctx);
                AppLog.Write($"processor audioCtx={ctx} ms={sw.ElapsedMilliseconds}");
            }
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _byContext.Values) p.Dispose();
        }
    }
}

/// <summary>エンジンとモデルを用意して読み込む(起動時に 1 回。常駐中は読み込んだまま)。engine が haas なら speech.cpp の worker(<see cref="SpeechEngineLoader"/>)。</summary>
internal static class EngineLoader
{
    /// <param name="prov">モデルの用意に使うもの(検証モードが取得元を差し替える)。省略時は <paramref name="modelsDir"/> の本物。</param>
    /// <param name="tools">haas のとき speech.cpp の用意に使うもの(検証モードが取得元を差し替える)。省略時は本物。</param>
    public static async Task<IEngine> LoadAsync(AppSettings s, string modelsDir, Action<string> onProgress, CancellationToken ct,
        ModelProvisioner? prov = null, SpeechToolProvisioner? tools = null)
    {
        if (Engines.IsHaas(s.Engine)) return await SpeechEngineLoader.LoadAsync(s, modelsDir, onProgress, ct, tools, prov, log: AppLog.Write);
        var entry = ModelCatalog.Get(s.Model);
        prov ??= new ModelProvisioner(modelsDir);
        var last = -1;
        var path = await prov.EnsureAsync(entry, new InlineProgress<(string Phase, double Ratio)>(p =>
        {
            int pct = (int)(p.Ratio * 100);
            if (pct != last) { last = pct; onProgress($"{p.Phase} {pct}%"); }
        }), ct);
        onProgress("読み込み");
        var t = await Task.Run(() => WhisperTranscriber.Load(path, entry.Name, s.SilenceThreshold, s.Backend, s.PreferSpeed, s.Keywords), ct);
        // 暖機: 最初の文字起こしは GPU の初期化(初回は CUDA カーネルの JIT も)で数秒〜十秒かかる。準備中のうちに無音 1 秒で済ませておく
        onProgress("暖機");
        await t.TranscribeAsync(new float[Audio.SampleRate], null, ct);
        return t;
    }
}
