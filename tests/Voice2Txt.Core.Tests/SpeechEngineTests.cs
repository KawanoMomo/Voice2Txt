using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>設定 engine と Haas エンジン(speech.cpp の worker)の境界。worker は偽物の stdin / stdout で確かめる。</summary>
public class SpeechEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v2t-speech-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- 設定 engine ----

    [Theory]
    [InlineData(null, "whisper")]
    [InlineData("", "whisper")]
    [InlineData("whisper", "whisper")]
    [InlineData(" HAAS ", "haas")]
    [InlineData("Haas-ja-v1.0(speech.cpp、日本語)", "haas")]
    [InlineData("parakeet", null)]
    public void Engine_Normalize(string? v, string? expected) => Assert.Equal(expected, Engines.Normalize(v));

    [Fact]
    public void Engine_DefaultIsWhisper_AndMissingKeyInFileStaysWhisper()
    {
        Assert.Equal("whisper", new AppSettings().Engine);
        var s = JsonSerializer.Deserialize<AppSettings>("""{"talkKey":"RMenu","model":"small"}""", AppSettings.Json)!;
        Assert.False(Engines.IsHaas(s.Engine));
        Assert.Equal("small", Engines.ModelName(s));
    }

    [Fact]
    public void Engine_InvalidWarnsAndUsesWhisper()
    {
        Assert.Null(Engines.InvalidWarning("haas"));
        Assert.Contains("Whisper を使います", Engines.InvalidWarning("parakeet"));
        Assert.False(Engines.IsHaas("parakeet"));
    }

    [Fact]
    public void TrayStatus_WhisperUnchanged_HaasShowsEngineAndModel()
    {
        Assert.Equal("待機中(モデル large-v3-turbo、CUDA)", Engines.IdleTrayStatus("whisper", "large-v3-turbo", "Cuda"));
        Assert.Equal("待機中(エンジン haas、モデル haas-ja-v1.0、Vulkan)", Engines.IdleTrayStatus("haas", "haas-ja-v1.0", "Vulkan"));
        Assert.Equal("haas-ja-v1.0", Engines.ModelName(new AppSettings { Engine = "haas", Model = "small" }));
        Assert.True(AppVersion.TrayText(Engines.IdleTrayStatus("haas", "haas-ja-v1.0", "Vulkan")).EndsWith("Vulkan)"));
    }

    [Fact]
    public void SettingsScreen_EngineChoice()
    {
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "engine", "haas"));
        Assert.Equal("haas", s.Engine);
        Assert.Equal("haas", SettingsSchema.Read(s, "engine"));
        Assert.Null(SettingsSchema.Apply(s, "engine", "Whisper(whisper.cpp、既定)"));
        Assert.Equal("whisper", s.Engine);
        Assert.Contains("選べません", SettingsSchema.Apply(s, "engine", "nemo"));
        Assert.Equal(["engine"], SettingsSchema.Changed(new AppSettings(), new AppSettings { Engine = "haas" }));
        var copy = new AppSettings(); copy.CopyFrom(new AppSettings { Engine = "haas" });
        Assert.Equal("haas", copy.Engine);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("cuda", "gpu")]
    [InlineData("vulkan", "gpu")]
    [InlineData("cpu", "cpu")]
    [InlineData("??", "auto")]
    public void Device_FromBackend(string backend, string device) => Assert.Equal(device, SpeechCatalog.Device(backend));

    [Theory]
    [InlineData("Vulkan0", "Vulkan")]
    [InlineData("CPU", "Cpu")]
    [InlineData("MTL0", "MTL0")]
    [InlineData(null, null)]
    public void Runtime_FromDevice(string? device, string? runtime) => Assert.Equal(runtime, SpeechCatalog.RuntimeOf(device));

    [Fact]
    public void Catalog_PinsSourcesAndHashes()
    {
        Assert.StartsWith("https://github.com/nyosegawa/speech.cpp/releases/download/v0.10.1/", SpeechCatalog.Tool.Url);
        Assert.Matches("^[0-9a-f]{64}$", SpeechCatalog.Tool.Sha256);
        Assert.Contains("/resolve/6b965d5007ba780585de51549d57a8c1c0d67c7e/", SpeechCatalog.HaasModel.Url); // 版はコミットで固定
        Assert.Matches("^[0-9a-f]{64}$", SpeechCatalog.HaasModel.Sha256);
        Assert.EndsWith(".gguf", SpeechCatalog.HaasModel.FileName);
    }

    // ---- worker との JSON Lines ----

    private static string Ready(string device = "Vulkan0") =>
        $$$"""{"type":"ready","protocol":4,"version":"0.10.1","defaults":{},"model":{"name":"Haas-ja-v1.0","task":"recognition","device":"{{{device}}}","threads":0}}""";

    [Fact]
    public async Task Ready_ReadsVersionAndDevice()
    {
        var c = new SpeechWorkerClient(new StringWriter(), new StringReader(Ready() + "\n"));
        await c.WaitReadyAsync(CancellationToken.None);
        Assert.Equal("0.10.1", c.Version);
        Assert.Equal(4, c.Protocol);
        Assert.Equal("Vulkan0", c.Device);
    }

    [Fact]
    public async Task Ready_FatalOrEofThrowsWithReason()
    {
        var fatal = new SpeechWorkerClient(new StringWriter(),
            new StringReader("""{"type":"fatal","error":{"code":"model_file","option":null,"message":"cannot open"}}""" + "\n"));
        var ex = await Assert.ThrowsAsync<SpeechWorkerException>(() => fatal.WaitReadyAsync(CancellationToken.None));
        Assert.Contains("cannot open", ex.Message);
        Assert.Contains("model_file", ex.Message);
        var eof = new SpeechWorkerClient(new StringWriter(), new StringReader(""));
        await Assert.ThrowsAsync<SpeechWorkerException>(() => eof.WaitReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_SendsChunksThenTranscribe_SkipsProgressAndOtherIds()
    {
        var input = new StringWriter();
        var output = new StringReader(string.Join("\n",
            """{"type":"progress","id":"r1","done":0.5}""",
            """{"type":"end","id":"zz","text":"別の要求"}""",
            """{"type":"end","id":"r1","text":"群島や湖では必ずしもヨットは必要ありません。","stop":"complete"}""") + "\n");
        var c = new SpeechWorkerClient(input, output);
        var audio = new float[Audio.SampleRate * 12]; // 12 秒 → 5 秒の chunk が 3 つ
        var text = await c.TranscribeAsync(audio, CancellationToken.None);
        Assert.Equal("群島や湖では必ずしもヨットは必要ありません。", text);

        var lines = input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(["chunk", "chunk", "chunk", "transcribe"], lines.Select(l => (string)l["type"]!));
        Assert.All(lines, l => Assert.Equal("r1", (string)l["id"]!));
        Assert.Equal([0, 1, 2], lines.Take(3).Select(l => (int)l["seq"]!));
        Assert.Equal(Audio.SampleRate, (int)lines[3]["sample_rate"]!);
        var total = lines.Take(3).Sum(l => Convert.FromBase64String((string)l["pcm"]!).Length);
        Assert.Equal(audio.Length * 2, total);
        Assert.DoesNotContain('\r', input.ToString());
    }

    [Fact]
    public async Task Transcribe_ErrorAndEarlyExitThrow()
    {
        var err = new SpeechWorkerClient(new StringWriter(),
            new StringReader("""{"type":"error","id":"r1","error":{"code":"out_of_range","option":"sample_rate","message":"bad rate"}}""" + "\n"));
        var ex = await Assert.ThrowsAsync<SpeechWorkerException>(() => err.TranscribeAsync(new float[1600], CancellationToken.None));
        Assert.Contains("bad rate", ex.Message);
        var eof = new SpeechWorkerClient(new StringWriter(), new StringReader(""));
        await Assert.ThrowsAsync<SpeechWorkerException>(() => eof.TranscribeAsync(new float[1600], CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_EmptyAudioIsNotSent()
    {
        var input = new StringWriter();
        Assert.Equal("", await new SpeechWorkerClient(input, new StringReader("")).TranscribeAsync([], CancellationToken.None));
        Assert.Equal("", input.ToString());
    }

    [Fact]
    public async Task Transcribe_IdsAdvancePerRequest()
    {
        var input = new StringWriter();
        var c = new SpeechWorkerClient(input, new StringReader(
            """{"type":"end","id":"r1","text":"一"}""" + "\n" + """{"type":"end","id":"r2","text":"二"}""" + "\n"));
        Assert.Equal("一", await c.TranscribeAsync(new float[100], CancellationToken.None));
        Assert.Equal("二", await c.TranscribeAsync(new float[100], CancellationToken.None));
    }

    [Fact]
    public void Pcm16_LittleEndianClamped()
    {
        var b = Convert.FromBase64String(SpeechWorkerClient.Pcm16Base64([0f, 1f, -1f, 2f, 0.5f]));
        short S(int i) => BitConverter.ToInt16(b, 2 * i);
        Assert.Equal(0, S(0));
        Assert.Equal(32767, S(1));
        Assert.Equal(-32767, S(2));
        Assert.Equal(32767, S(3)); // 1 を超える値は切り詰める
        Assert.Equal(16384, S(4));
    }

    // ---- speech.cpp の用意 ----

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(files.TryGetValue(r.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] ToolZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var n in new[] { "speech.exe", "speech.dll", "speech.h" })
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open());
                w.Write("bin:" + n);
            }
        }
        return ms.ToArray();
    }

    private static SpeechToolRelease Rel(byte[] zip) =>
        new("9.9.9", "speech-9.9.9.zip", "https://example.invalid/speech-9.9.9.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length);

    [Fact]
    public async Task Tool_PathWinsWithoutFetching()
    {
        var server = new Server([]);
        var p = new SpeechToolProvisioner(_dir, new HttpClient(server), () => @"C:\bin\speech.exe");
        Assert.Equal(@"C:\bin\speech.exe", await p.EnsureAsync(null, CancellationToken.None));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Tool_FetchesVerifiesExtractsOnce()
    {
        var zip = ToolZip();
        var rel = Rel(zip);
        var server = new Server(new() { [rel.Url] = zip });
        var phases = new List<string>();
        var p = new SpeechToolProvisioner(_dir, new HttpClient(server), () => null, rel);
        var exe = await p.EnsureAsync(new InlineProgress<(string Phase, double Ratio)>(x => phases.Add(x.Phase)), CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "speech.exe"), exe);
        Assert.True(File.Exists(Path.Combine(_dir, "speech.dll")));
        Assert.Contains("ダウンロード", phases);
        Assert.Contains("展開", phases);
        Assert.False(Directory.Exists(Path.Combine(_dir, "download"))); // 書庫は残さない
        Assert.Equal(exe, await new SpeechToolProvisioner(_dir, new HttpClient(server), () => null, rel).EnsureAsync(null, CancellationToken.None));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Tool_HashMismatchThrows()
    {
        var zip = ToolZip();
        var rel = Rel(zip) with { Sha256 = new string('0', 64) };
        var p = new SpeechToolProvisioner(_dir, new HttpClient(new Server(new() { [rel.Url] = zip })), () => null, rel);
        await Assert.ThrowsAsync<InvalidDataException>(() => p.EnsureAsync(null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "speech.exe")));
    }

    // ---- Haas エンジンの用意(取得失敗は Whisper に落ちず理由を出す)----

    private sealed class FakeEngine : IEngine
    {
        public int Calls;
        public bool Disposed;
        public string Runtime => "Vulkan";
        public Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct) { Calls++; return Task.FromResult(""); }
        public void Dispose() => Disposed = true;
    }

    private ModelProvisioner LocalModel()
    {
        var models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(models);
        return new ModelProvisioner(models, new HttpClient(new Server([])));
    }

    [Fact]
    public async Task Load_ToolUnreachable_ThrowsReasonWithoutFallback()
    {
        var tools = new SpeechToolProvisioner(Path.Combine(_dir, "tools"), new HttpClient(new Server([])), () => null);
        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => SpeechEngineLoader.LoadAsync(
            new AppSettings { Engine = "haas" }, Path.Combine(_dir, "models"), _ => { }, CancellationToken.None, tools, LocalModel(),
            (_, _, _, _, _) => throw new InvalidOperationException("起動してはならない")));
        Assert.Contains("speech.cpp を用意できません", ex.Message);
        Assert.Contains("Whisper には切り替えません", ex.Message);
    }

    [Fact]
    public async Task Load_ModelUnreachable_ThrowsReason()
    {
        var tools = new SpeechToolProvisioner(Path.Combine(_dir, "tools"), null, () => @"C:\bin\speech.exe");
        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => SpeechEngineLoader.LoadAsync(
            new AppSettings { Engine = "haas" }, Path.Combine(_dir, "models"), _ => { }, CancellationToken.None, tools, LocalModel()));
        Assert.Contains("Haas-ja-v1.0 の重みを用意できません", ex.Message);
    }

    [Fact]
    public async Task Load_StartsWorkerWithModelAndDevice_ThenWarmsUp()
    {
        var models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(models);
        var weights = new byte[] { 1, 2, 3 };
        var entry = SpeechCatalog.HaasModel;
        var server = new Server(new() { [entry.Url] = weights });
        // 取得元のハッシュは本物の重みのものなので、偽の重みでは一致しない → 置き場に置いて印を付けた状態から始める
        File.WriteAllBytes(Path.Combine(models, entry.FileName), weights);
        var fi = new FileInfo(Path.Combine(models, entry.FileName));
        File.WriteAllText(Path.Combine(models, entry.FileName) + ".verified", $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{entry.Sha256}");
        var engine = new FakeEngine();
        string? gotExe = null, gotModel = null, gotDevice = null, gotKeywords = "unset";
        var progress = new List<string>();
        var t = await SpeechEngineLoader.LoadAsync(new AppSettings { Engine = "haas", Backend = "cpu", Keywords = ["Haas = ハース , ハウス", "AIモデル", "bad=", ""] }, models, progress.Add, CancellationToken.None,
            new SpeechToolProvisioner(Path.Combine(_dir, "tools"), null, () => @"C:\bin\speech.exe"), new ModelProvisioner(models, new HttpClient(server)),
            (exe, model, device, kw, _) => { gotExe = exe; gotModel = model; gotDevice = device; gotKeywords = kw; return Task.FromResult<IEngine>(engine); });
        Assert.Same(engine, t);
        Assert.Equal(@"C:\bin\speech.exe", gotExe);
        Assert.Equal(Path.Combine(models, entry.FileName), gotModel);
        Assert.Equal("cpu", gotDevice);
        Assert.Equal("Haas=ハース,ハウス\nAIモデル", gotKeywords); // 読めない語は渡さない
        Assert.Equal(1, engine.Calls); // 暖機
        Assert.Equal(["読み込み", "暖機"], progress.TakeLast(2));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Load_WorkerStartFails_ThrowsReason()
    {
        var models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(models);
        var entry = SpeechCatalog.HaasModel;
        File.WriteAllBytes(Path.Combine(models, entry.FileName), [1]);
        var fi = new FileInfo(Path.Combine(models, entry.FileName));
        File.WriteAllText(Path.Combine(models, entry.FileName) + ".verified", $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{entry.Sha256}");
        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => SpeechEngineLoader.LoadAsync(
            new AppSettings { Engine = "haas" }, models, _ => { }, CancellationToken.None,
            new SpeechToolProvisioner(Path.Combine(_dir, "tools"), null, () => @"C:\bin\speech.exe"), new ModelProvisioner(models),
            (_, _, _, _, _) => throw new SpeechWorkerException("speech worker を起動できない: no Vulkan")));
        Assert.Contains("worker を起動できません", ex.Message);
        Assert.Contains("no Vulkan", ex.Message);
    }

    // ---- カスタム語彙(BLK-human-20261102-1015-2)----

    [Theory]
    [InlineData("Haas", "Haas")]
    [InlineData(" Haas = ハース , ハウス ", "Haas=ハース,ハウス")]
    [InlineData("Third Intelligence=サードインテリジェンス", "Third Intelligence=サードインテリジェンス")]
    [InlineData("=ハース", null)]
    [InlineData("Haas=", null)]
    [InlineData("Haas=ハース,", null)]
    [InlineData("a=b=c", null)]
    [InlineData("  ", null)]
    public void Keywords_Normalize(string word, string? expected) => Assert.Equal(expected, HaasKeywords.Normalize(word));

    [Fact]
    public void Keywords_JoinOnePerLine_EmptyIsNull()
    {
        Assert.Null(HaasKeywords.Join(null));
        Assert.Null(HaasKeywords.Join([]));
        Assert.Null(HaasKeywords.Join(["=x"]));
        Assert.Equal("Haas=ハース\nnyosegawa=ニョセガワ", HaasKeywords.Join(["Haas=ハース", "nyosegawa=ニョセガワ", "Haas=ハース"]));
        Assert.Null(HaasKeywords.InvalidWarning(["Haas=ハース"]));
        Assert.Contains("「a=b=c」", HaasKeywords.InvalidWarning(["Haas", "a=b=c"]));
    }

    [Fact]
    public void Keywords_SettingsScreen_KeepsReadingCommasAndInnerSpaces()
    {
        var s = new AppSettings();
        Assert.Empty(s.Keywords);
        Assert.Null(SettingsSchema.Apply(s, "keywords", "Haas=ハース,ハウス、Third Intelligence、ニョセガワ"));
        Assert.Equal(["Haas=ハース,ハウス", "Third Intelligence", "ニョセガワ"], s.Keywords);
        Assert.Equal("Haas=ハース,ハウス、Third Intelligence、ニョセガワ", SettingsSchema.Read(s, "keywords"));
        Assert.Contains("読めない", SettingsSchema.Apply(s, "keywords", "Haas="));
        Assert.Equal(3, s.Keywords.Count); // 読めなければ変えない
        Assert.Null(SettingsSchema.Apply(s, "keywords", ""));
        Assert.Empty(s.Keywords);
        var copy = new AppSettings(); copy.CopyFrom(new AppSettings { Keywords = ["Haas"] });
        Assert.Equal(["Haas"], copy.Keywords);
        var fromFile = JsonSerializer.Deserialize<AppSettings>("""{"engine":"haas"}""", AppSettings.Json)!;
        Assert.Empty(fromFile.Keywords);
    }

    [Fact]
    public async Task Keywords_SentOnTranscribeOnlyWhenGiven()
    {
        var input = new StringWriter();
        var c = new SpeechWorkerClient(input, new StringReader(
            """{"type":"end","id":"r1","text":"Haas"}""" + "\n" + """{"type":"end","id":"r2","text":"ハース"}""" + "\n"));
        await c.TranscribeAsync(new float[100], CancellationToken.None, "Haas=ハース\nAIモデル");
        await c.TranscribeAsync(new float[100], CancellationToken.None);
        var tx = input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(o => (string)o["type"]! == "transcribe").ToList();
        Assert.Equal("Haas=ハース\nAIモデル", (string)tx[0]["keywords"]!);
        Assert.False(tx[1].ContainsKey("keywords"));
        Assert.False(tx[0].ContainsKey("timestamps")); // keywords と timestamps は併用できない
    }
}
