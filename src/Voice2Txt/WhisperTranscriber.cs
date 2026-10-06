using System.Text;
using Voice2Txt.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Voice2Txt;

/// <summary>whisper.cpp(Whisper.net)をプロセス内に取り込む。日本語固定。バックエンドは CUDA を先に試す。</summary>
internal sealed class WhisperTranscriber : ITranscriber, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _one = new(1, 1);

    public string Runtime { get; }

    private WhisperTranscriber(WhisperFactory f, WhisperProcessor p, string runtime) { _factory = f; _processor = p; Runtime = runtime; }

    public static WhisperTranscriber Load(string modelPath)
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
        var f = WhisperFactory.FromPath(modelPath);
        var p = f.CreateBuilder().WithLanguage("ja").Build();
        var rt = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        return new WhisperTranscriber(f, p, rt);
    }

    public async Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var sb = new StringBuilder();
            await foreach (var seg in _processor.ProcessAsync(samples16k, ct)) sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { _one.Release(); }
    }

    public void Dispose()
    {
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
        return await Task.Run(() => WhisperTranscriber.Load(path), ct);
    }
}
