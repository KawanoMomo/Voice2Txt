using System.Text;
using Voice2Txt.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Voice2Txt;

/// <summary>
/// whisper.cpp(Whisper.net)をプロセス内に取り込む。日本語固定。バックエンドは CUDA を先に試す。
/// 語の時刻(DTW)を出せるモデルでは、確定版の文字が無い声の区間を拾い直す(<see cref="Recovery"/>)。
/// </summary>
internal sealed class WhisperTranscriber : ITranscriber, ISpanDecoder, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly WhisperProcessor? _recover;
    private readonly double _silenceThreshold;
    private readonly SemaphoreSlim _one = new(1, 1);

    public string Runtime { get; }

    private WhisperTranscriber(WhisperFactory f, WhisperProcessor p, WhisperProcessor? recover, double silenceThreshold, string runtime)
    {
        _factory = f; _processor = p; _recover = recover; _silenceThreshold = silenceThreshold; Runtime = runtime;
    }

    /// <summary>
    /// モデルを読み込む。<paramref name="modelName"/> の語の時刻の当て方(alignment heads)が分かれば DTW を有効にして拾い直しを使う。
    /// DTW を有効にした whisper.cpp は 30 秒を超える入力の 2 つ目以降の窓を返さないので、長い発話は <see cref="Recovery.Windows"/> で切って渡す。
    /// </summary>
    public static WhisperTranscriber Load(string modelPath, string? modelName = null, double silenceThreshold = 0.01)
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
        var heads = AlignmentHeads(modelName);
        var f = heads is { } h
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = h })
            : WhisperFactory.FromPath(modelPath);
        var p = f.CreateBuilder().WithLanguage(Decoding.Language).WithPrompt(Decoding.InitialPrompt).WithTokenTimestamps().Build();
        var r = heads is null ? null : f.CreateBuilder().WithLanguage("auto").WithNoContext().Build();
        var rt = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        return new WhisperTranscriber(f, p, r, silenceThreshold, rt);
    }

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

    public async Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (_recover is null)
            {
                var sb = new StringBuilder();
                await foreach (var seg in _processor.ProcessAsync(samples16k, ct)) sb.Append(seg.Text);
                return sb.ToString().Trim();
            }
            var r = await Recovery.TranscribeAsync(samples16k, _silenceThreshold, this, ct);
            if (r.Recovered > 0) AppLog.Write($"recovered spans={r.Recovered}"); // 本文は書かない
            return r.Text;
        }
        finally { _one.Release(); }
    }

    // ISpanDecoder: TranscribeAsync が _one を持ったまま呼ぶ(外から直接は呼ばない)
    async Task<IReadOnlyList<DecodedSegment>> ISpanDecoder.DecodeAsync(float[] samples, CancellationToken ct)
    {
        var res = new List<DecodedSegment>();
        await foreach (var seg in _processor.ProcessAsync(samples, ct))
            res.Add(new DecodedSegment(seg.Text, seg.Tokens.Select(t => new TimedToken(t.DtwTimestamp < 0 ? -1 : t.DtwTimestamp / 100.0, t.Text ?? "")).ToList()));
        return res;
    }

    async Task<(string Text, double NoSpeech)> ISpanDecoder.DecodeSpanAsync(float[] samples, CancellationToken ct)
    {
        var sb = new StringBuilder();
        double noSpeech = 0;
        await foreach (var seg in _recover!.ProcessAsync(samples, ct)) { sb.Append(seg.Text); noSpeech = Math.Max(noSpeech, seg.NoSpeechProbability); }
        return (sb.ToString(), noSpeech);
    }

    public void Dispose()
    {
        _recover?.Dispose();
        _processor.Dispose();
        _factory.Dispose();
    }
}

/// <summary>モデルを用意して読み込む(起動時に 1 回。常駐中は読み込んだまま)。</summary>
internal static class EngineLoader
{
    public static async Task<WhisperTranscriber> LoadAsync(AppSettings s, string modelsDir, Action<string> onProgress, CancellationToken ct)
    {
        var entry = ModelCatalog.Get(s.Model);
        var prov = new ModelProvisioner(modelsDir);
        var last = -1;
        var path = await prov.EnsureAsync(entry, new Progress<(string Phase, double Ratio)>(p =>
        {
            int pct = (int)(p.Ratio * 100);
            if (pct != last) { last = pct; onProgress($"{p.Phase} {pct}%"); }
        }), ct);
        onProgress("読み込み");
        var t = await Task.Run(() => WhisperTranscriber.Load(path, entry.Name, s.SilenceThreshold), ct);
        // 暖機: 最初の文字起こしは GPU の初期化(初回は CUDA カーネルの JIT も)で数秒〜十秒かかる。準備中のうちに無音 1 秒で済ませておく
        onProgress("暖機");
        await t.TranscribeAsync(new float[Audio.SampleRate], null, ct);
        return t;
    }
}
