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

    private WhisperTranscriber(WhisperFactory f, ProcessorPool p, ProcessorPool? recover, double silenceThreshold, string runtime)
    {
        _factory = f; _processor = p; _recover = recover; _silenceThreshold = silenceThreshold; Runtime = runtime; _fit = p.Fit;
    }

    /// <summary>
    /// モデルを読み込む。<paramref name="modelName"/> の語の時刻の当て方(alignment heads)が分かれば DTW を有効にして拾い直しを使う。
    /// DTW を有効にした whisper.cpp は窓を送る途中で返し終えることがある(30 秒前後より長い入力で後ろが黙って消える)ので、
    /// <see cref="Recovery.TranscribeAsync"/> が最後の区切りの終わりから続きを渡し直す。
    /// </summary>
    /// <param name="preferSpeed">設定 preferSpeed。真なら復号ごとに、渡す音声の長さに縮めた窓(<see cref="Decoding.AudioContext"/>)の処理器を使う。</param>
    public static WhisperTranscriber Load(string modelPath, string? modelName = null, double silenceThreshold = 0.01, string? backend = null, bool preferSpeed = false)
    {
        // バックエンドは設定 backend の順に読めるものを使う(auto: CUDA → Vulkan → CPU)。プロセスで最初に読めたものが使われ続ける
        RuntimeOptions.RuntimeLibraryOrder = Backends.Order(backend).Select(n => Enum.Parse<RuntimeLibrary>(n)).ToList();
        Recovery.Diag ??= AppLog.Write;
        var heads = AlignmentHeads(modelName);
        var f = heads is { } h
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = h })
            : WhisperFactory.FromPath(modelPath);
        var p = new ProcessorPool(ctx => WithContext(f.CreateBuilder().WithLanguage(Decoding.Language).WithPrompt(Decoding.InitialPrompt).WithTokenTimestamps(), ctx).Build(), preferSpeed);
        var r = heads is null ? null : new ProcessorPool(ctx => WithContext(f.CreateBuilder().WithLanguage("auto").WithNoContext(), ctx).Build(), preferSpeed);
        var rt = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        return new WhisperTranscriber(f, p, r, silenceThreshold, rt);
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
                foreach (var seg in await DecodeGuardedAsync(_processor.For(samples16k), samples16k, _fit ? _decoded : null, ct)) sb.Append(seg.Text);
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
        foreach (var seg in await DecodeGuardedAsync(_processor.For(samples), samples, _fit ? _decoded : null, ct))
            res.Add(new DecodedSegment(seg.Text, seg.Tokens, seg.EndSeconds));
        return res;
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
        var t = await Task.Run(() => WhisperTranscriber.Load(path, entry.Name, s.SilenceThreshold, s.Backend, s.PreferSpeed), ct);
        // 暖機: 最初の文字起こしは GPU の初期化(初回は CUDA カーネルの JIT も)で数秒〜十秒かかる。準備中のうちに無音 1 秒で済ませておく
        onProgress("暖機");
        await t.TranscribeAsync(new float[Audio.SampleRate], null, ct);
        return t;
    }
}
