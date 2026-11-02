using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voice2Txt.Core;

/// <summary>speech.cpp の配布物(release の書庫 1 つ)。取得元・版・ハッシュはここに固定する(THIRD_PARTY_NOTICES.md と同じ)。</summary>
public sealed record SpeechToolRelease(string Version, string FileName, string Url, string Sha256, long Size);

/// <summary>
/// Haas エンジンの取得元: speech.cpp(nyosegawa/speech.cpp、MIT、Vulkan)の Windows 版の release と、
/// Haas-ja-v1.0 の GGUF の重み(sakasegawa/Haas-ja-v1.0-GGUF、Apache-2.0。版はリポジトリのコミットで固定)。
/// </summary>
public static class SpeechCatalog
{
    public const string ExeName = "speech.exe";

    public static readonly SpeechToolRelease Tool = new("0.10.1", "speech-0.10.1-windows-x64-vulkan.zip",
        "https://github.com/nyosegawa/speech.cpp/releases/download/v0.10.1/speech-0.10.1-windows-x64-vulkan.zip",
        "a8fe0e4b4f463c460e7ace9d38c363bec08b254cac5a7f764b7967af35d4fa1a", 29548632);

    /// <summary>既定の重み Q8_0(0.66 GB)。置き場は Whisper のモデルと同じ %APPDATA%\Voice2Txt\models。</summary>
    public static readonly ModelEntry HaasModel = new("haas-ja-v1.0", "Haas-616M-ja-v1.0-Q8_0.gguf",
        "https://huggingface.co/sakasegawa/Haas-ja-v1.0-GGUF/resolve/6b965d5007ba780585de51549d57a8c1c0d67c7e/Haas-616M-ja-v1.0-Q8_0.gguf",
        "cd5f60b2f1c411404b83fca4a65e3ce30a26d9e951b6a346529e65f22758afeb", 656381472);

    /// <summary>取得した speech.cpp の置き場(%APPDATA%\Voice2Txt\tools\speech)。</summary>
    public static string DefaultToolDirectory => Path.Combine(AppSettings.DefaultDirectory, "tools", "speech");

    /// <summary>設定 backend を speech.cpp の --device へ(speech.cpp に CUDA は無いので cuda / vulkan は GPU)。</summary>
    public static string Device(string? backend) => Backends.Normalize(backend) switch
    {
        "cpu" => "cpu",
        "cuda" or "vulkan" => "gpu",
        _ => "auto",
    };

    /// <summary>worker の ready が返す device(例 Vulkan0 / CPU)を、結果・ツールチップの runtime の名前(Vulkan / Cpu)へ。</summary>
    public static string? RuntimeOf(string? device) => device switch
    {
        null or "" => null,
        _ when device.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase) => Backends.Vulkan,
        _ when device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) => Backends.Cpu,
        _ => device,
    };
}

/// <summary>
/// speech.cpp の exe を用意する: PATH に speech.exe があればそれを使う。無ければ置き場に取得済みのもの(印 .verified-{版})を使い、
/// それも無ければ release の書庫を取得元からダウンロードして SHA-256 を照合し(<see cref="ModelProvisioner"/> と同じ経路)、置き場に展開する。
/// </summary>
public sealed class SpeechToolProvisioner(string toolDir, HttpClient? http = null, Func<string?>? findOnPath = null, SpeechToolRelease? release = null)
{
    private readonly SpeechToolRelease _rel = release ?? SpeechCatalog.Tool;
    public string ToolDir => toolDir;
    public string ExePath => Path.Combine(toolDir, SpeechCatalog.ExeName);
    private string Marker => Path.Combine(toolDir, ".verified-" + _rel.Version);

    /// <param name="progress">(段階, 0.0〜1.0)。段階は「ダウンロード」「検証」「展開」。</param>
    /// <returns>使う speech.exe のフルパス。</returns>
    public async Task<string> EnsureAsync(IProgress<(string Phase, double Ratio)>? progress, CancellationToken ct)
    {
        if ((findOnPath ?? FindOnPath)() is { } onPath) return onPath;
        if (File.Exists(ExePath) && File.Exists(Marker) && File.ReadAllText(Marker) == _rel.Sha256) return ExePath;
        var downloads = Path.Combine(toolDir, "download");
        var zip = await new ModelProvisioner(downloads, http)
            .EnsureAsync(new ModelEntry("speech.cpp", _rel.FileName, _rel.Url, _rel.Sha256, _rel.Size), progress, ct);
        progress?.Report(("展開", 0));
        using (var z = ZipFile.OpenRead(zip))
        {
            foreach (var e in z.Entries.Where(e => e.Name.Length > 0))
            {
                var dst = Path.GetFullPath(Path.Combine(toolDir, e.FullName));
                if (!dst.StartsWith(Path.GetFullPath(toolDir), StringComparison.OrdinalIgnoreCase)) continue; // 書庫の外を指す名前は置かない
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                e.ExtractToFile(dst + ".part", overwrite: true);
                File.Move(dst + ".part", dst, overwrite: true);
            }
        }
        if (!File.Exists(ExePath)) throw new InvalidDataException($"{_rel.FileName} に {SpeechCatalog.ExeName} が無い");
        File.WriteAllText(Marker, _rel.Sha256);
        try { Directory.Delete(downloads, true); } catch (IOException) { }
        return ExePath;
    }

    /// <summary>PATH にある speech.exe(無ければ null)。</summary>
    public static string? FindOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => { try { return Path.Combine(d.Trim('"'), SpeechCatalog.ExeName); } catch (ArgumentException) { return null; } })
            .FirstOrDefault(p => p is not null && File.Exists(p));
}

/// <summary>
/// Haas のカスタム語彙(設定 keywords)を speech.cpp の request option <c>keywords</c>(1 行 1 語、「表記=読み,読み」)へ。
/// 読めない語(空の表記・空の読み・= が 2 つ以上)は渡さない(worker が要求ごとに誤りを返し、確定版が届かなくなるのを防ぐ)。
/// </summary>
public static class HaasKeywords
{
    /// <summary>画面・設定の値を語の列へ(読点・改行・タブで区切る。読みの「,」と語の中の空白は残す。空は除き、重複は 1 つに)。</summary>
    public static List<string> Split(string v) =>
        v.Split(['、', '､', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    /// <summary>1 語を整える(表記と読みの前後の空白を除く)。読めなければ null。</summary>
    public static string? Normalize(string? word)
    {
        var w = (word ?? "").Trim();
        if (w.Length == 0 || w.Contains('\n') || w.Contains('\r')) return null;
        var parts = w.Split('=');
        if (parts.Length == 1) return w;
        if (parts.Length > 2) return null;
        var written = parts[0].Trim();
        var readings = parts[1].Split([',', '，', '、']).Select(r => r.Trim()).ToList();
        if (written.Length == 0 || readings.Any(r => r.Length == 0)) return null;
        return $"{written}={string.Join(",", readings)}";
    }

    /// <summary>worker に渡す keywords(読める語を 1 行 1 語で。無ければ null = 渡さない)。</summary>
    public static string? Join(IEnumerable<string>? words)
    {
        var ok = (words ?? []).Select(Normalize).OfType<string>().Distinct().ToList();
        return ok.Count == 0 ? null : string.Join("\n", ok);
    }

    /// <summary>読めない語があるときの知らせ(無ければ null)。</summary>
    public static string? InvalidWarning(IEnumerable<string>? words)
    {
        var bad = (words ?? []).Where(w => Normalize(w) is null).ToList();
        return bad.Count == 0 ? null
            : $"カスタム語彙の「{string.Join("」「", bad)}」は読めないため使いません(1 語は「表記」か「表記=よみ,よみ」)";
    }
}

/// <summary>speech.cpp の worker が返した誤り(fatal / error / 途中で終わった)。</summary>
public sealed class SpeechWorkerException(string message) : Exception(message);

/// <summary>
/// <c>speech worker MODEL</c> と JSON Lines(docs/worker.md、protocol 4)で話す。要求は 1 つずつ: 音声を <c>chunk</c> 行(16 bit PCM の base64)で送り、
/// <c>transcribe</c> 行で確定させ、<c>end</c> の <c>text</c> を返す。<c>progress</c> は読み飛ばす。取り消しは <c>cancel</c> を送り <c>cancelled</c> を待つ。
/// </summary>
public sealed class SpeechWorkerClient(TextWriter input, TextReader output)
{
    /// <summary>1 つの chunk 行に入れるサンプル数(5 秒)。</summary>
    public const int ChunkSamples = Audio.SampleRate * 5;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _write = new();
    private int _next;

    public string? Version { get; private set; }
    /// <summary>worker が読み込んだ先(例 Vulkan0 / CPU)。</summary>
    public string? Device { get; private set; }
    public int? Protocol { get; private set; }

    /// <summary>最初の行(ready か fatal)を待つ。fatal・途中で終わったら <see cref="SpeechWorkerException"/>。</summary>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        var line = await output.ReadLineAsync(ct) ?? throw new SpeechWorkerException("speech worker が ready を返さずに終わった");
        var o = ParseObject(line);
        switch ((string?)o["type"])
        {
            case "ready":
                Version = (string?)o["version"];
                Protocol = o["protocol"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : null;
                Device = o["model"]?["device"] is JsonValue d && d.TryGetValue<string>(out var s) ? s : null;
                return;
            case "fatal":
                throw new SpeechWorkerException("speech worker を起動できない: " + Describe(o["error"]));
            default:
                throw new SpeechWorkerException($"speech worker の最初の行が ready でない(type={(string?)o["type"]})");
        }
    }

    /// <summary>16 kHz モノラルの音声を文字起こしする(要求は 1 つずつ。空の音声は送らず空文字)。</summary>
    /// <param name="keywords">カスタム語彙(<see cref="HaasKeywords.Join"/> の形。null なら渡さない)。</param>
    public async Task<string> TranscribeAsync(float[] samples16k, CancellationToken ct, string? keywords = null)
    {
        if (samples16k.Length == 0) return "";
        await _one.WaitAsync(ct);
        try
        {
            var id = "r" + Interlocked.Increment(ref _next);
            int seq = 0;
            for (int i = 0; i < samples16k.Length; i += ChunkSamples)
            {
                var pcm = Pcm16Base64(samples16k.AsSpan(i, Math.Min(ChunkSamples, samples16k.Length - i)));
                Send(new JsonObject { ["type"] = "chunk", ["id"] = id, ["seq"] = seq++, ["pcm"] = pcm });
            }
            var req = new JsonObject { ["type"] = "transcribe", ["id"] = id, ["sample_rate"] = Audio.SampleRate };
            if (!string.IsNullOrEmpty(keywords)) req["keywords"] = keywords; // timestamps は使わない(keywords と併用できない)
            Send(req);
            using var reg = ct.Register(() => { try { Send(new JsonObject { ["type"] = "cancel", ["id"] = id }); } catch (Exception) { } });
            while (true)
            {
                // 取り消しても cancelled が届くまで読む(次の要求の行と取り違えない)
                var line = await output.ReadLineAsync(CancellationToken.None) ?? throw new SpeechWorkerException("speech worker が文字起こしの途中で終わった");
                var o = ParseObject(line);
                var type = (string?)o["type"];
                var rid = o["id"] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
                if (rid is null && type == "error") throw new SpeechWorkerException("speech worker の誤り: " + Describe(o["error"]));
                if (rid != id) continue;
                switch (type)
                {
                    case "progress": continue;
                    case "end": return (string?)o["text"] ?? "";
                    case "error": throw new SpeechWorkerException("speech worker の誤り: " + Describe(o["error"]));
                    case "cancelled": throw new OperationCanceledException(ct);
                    default: continue;
                }
            }
        }
        finally { _one.Release(); }
    }

    private void Send(JsonObject o)
    {
        lock (_write)
        {
            input.Write(o.ToJsonString());
            input.Write('\n');
            input.Flush();
        }
    }

    /// <summary>16 bit リトルエンディアンの PCM を base64 にする(各サンプル round(clamp(x, −1, 1) × 32767))。</summary>
    public static string Pcm16Base64(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            bytes[2 * i] = (byte)(v & 0xFF);
            bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return Convert.ToBase64String(bytes);
    }

    private static JsonObject ParseObject(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject ?? throw new SpeechWorkerException("speech worker の出力が JSON の object でない");
        }
        catch (JsonException) { throw new SpeechWorkerException("speech worker の出力が JSON でない"); }
    }

    private static string Describe(JsonNode? err) =>
        err is null ? "(理由なし)" : $"{(string?)err["message"]}(code={(string?)err["code"]}{(err["option"] is { } op ? $", option={op}" : "")})";
}

/// <summary>
/// Haas エンジン: speech.cpp の worker を常駐プロセスにして JSON Lines で往復する(モデルは読み込んだまま)。
/// 途中経過も確定版と同じ経路(押下中の写しを同じ worker で文字起こしし直す)。worker は stdin が閉じれば自分で終わる。
/// </summary>
public sealed class SpeechWorkerTranscriber : IEngine
{
    private readonly Process _proc;
    private readonly SpeechWorkerClient _client;
    private readonly string? _keywords;

    public string Runtime { get; }
    public string? Version => _client.Version;

    private SpeechWorkerTranscriber(Process p, SpeechWorkerClient c, string runtime, string? keywords) { _proc = p; _client = c; Runtime = runtime; _keywords = keywords; }

    /// <summary>worker を起動し、ready(モデルの読み込みと暖機)を待つ。起動できなければ worker の標準エラーの末尾を添えて投げる。</summary>
    /// <param name="keywords">要求ごとに渡すカスタム語彙(<see cref="HaasKeywords.Join"/>。null なら渡さない)。</param>
    public static async Task<SpeechWorkerTranscriber> StartAsync(string exe, string modelPath, string device, string? keywords, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            ArgumentList = { "worker", modelPath, "--device", device },
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), // BOM を付けると最初の行が JSON でなくなる
        };
        psi.Environment["SPEECH_NO_UPDATE_CHECK"] = "1"; // 外へ問い合わせない
        var p = Process.Start(psi) ?? throw new SpeechWorkerException("speech worker を起動できない");
        var tail = new Queue<string>();
        // 標準エラー(worker のログ)は読み捨てる(詰まらせない)。起動に失敗したときの理由に末尾だけ使う。文字起こしの本文は出ない
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (tail) { tail.Enqueue(l); while (tail.Count > 5) tail.Dequeue(); } };
        p.BeginErrorReadLine();
        var c = new SpeechWorkerClient(p.StandardInput, p.StandardOutput);
        try { await c.WaitReadyAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            string why; lock (tail) why = string.Join(" / ", tail);
            throw new SpeechWorkerException(ex.Message + (why.Length > 0 ? $"(stderr: {why})" : ""));
        }
        return new SpeechWorkerTranscriber(p, c, SpeechCatalog.RuntimeOf(c.Device) ?? "unknown", keywords);
    }

    public Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct)
    {
        if (_proc.HasExited) throw new SpeechWorkerException($"speech worker が終わっている(終了コード {_proc.ExitCode})");
        return _client.TranscribeAsync(samples16k, ct, _keywords);
    }

    public void Dispose()
    {
        try
        {
            _proc.StandardInput.Close(); // worker は stdin が閉じれば残りを答えて終わる
            if (!_proc.WaitForExit(2000)) _proc.Kill();
        }
        catch (Exception) { }
        _proc.Dispose();
    }
}

/// <summary>Haas エンジンを用意する: speech.cpp → 重み → worker の起動 → 暖機。どこかで用意できなければ理由を付けて <see cref="EngineUnavailableException"/>。</summary>
public static class SpeechEngineLoader
{
    public const string NoFallback = "Whisper には切り替えません。設定 engine を whisper に戻すか、取得できる状態にして再起動してください";

    public static async Task<IEngine> LoadAsync(AppSettings s, string modelsDir, Action<string> onProgress, CancellationToken ct,
        SpeechToolProvisioner? tools = null, ModelProvisioner? models = null,
        Func<string, string, string, string?, CancellationToken, Task<IEngine>>? start = null, Action<string>? log = null)
    {
        tools ??= new SpeechToolProvisioner(SpeechCatalog.DefaultToolDirectory);
        models ??= new ModelProvisioner(modelsDir);
        start ??= async (exe, model, device, kw, c) => await SpeechWorkerTranscriber.StartAsync(exe, model, device, kw, c);
        string exe, model;
        try { exe = await tools.EnsureAsync(Percent("speech.cpp ", onProgress), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new EngineUnavailableException($"speech.cpp を用意できません({ex.Message})。{NoFallback}", ex); }
        try { model = await models.EnsureAsync(SpeechCatalog.HaasModel, Percent("", onProgress), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new EngineUnavailableException($"Haas-ja-v1.0 の重みを用意できません({ex.Message})。{NoFallback}", ex); }
        onProgress("読み込み");
        IEngine t;
        try { t = await start(exe, model, SpeechCatalog.Device(s.Backend), HaasKeywords.Join(s.Keywords), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new EngineUnavailableException($"speech.cpp の worker を起動できません({ex.Message})。{NoFallback}", ex); }
        onProgress("暖機"); // カスタム語彙も渡すので、worker が語彙を受け付けない(古い重みなど)なら、ここで理由が分かる
        try { await t.TranscribeAsync(new float[Audio.SampleRate], null, ct); }
        catch (Exception ex)
        {
            t.Dispose();
            if (ex is OperationCanceledException) throw;
            throw new EngineUnavailableException($"speech.cpp の worker で文字起こしできません({ex.Message})。{NoFallback}", ex);
        }
        // 30 秒を超える発話は声の区間ごとに渡す(1 回に文がいくつも入ると Haas は文を丸ごと落とす)
        return new RegionTranscriber(t, s.SilenceThreshold, log);
    }

    /// <summary>(段階, 0〜1)を「{接頭}{段階} N%」の準備の文へ(1% ごと)。</summary>
    private static IProgress<(string Phase, double Ratio)> Percent(string prefix, Action<string> onProgress)
    {
        var last = (Phase: "", Pct: -1);
        return new InlineProgress<(string Phase, double Ratio)>(p =>
        {
            int pct = (int)(p.Ratio * 100);
            if ((p.Phase, pct) == last) return;
            last = (p.Phase, pct);
            onProgress($"{prefix}{p.Phase} {pct}%");
        });
    }
}
