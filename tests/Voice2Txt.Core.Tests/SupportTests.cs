using System.Net;
using System.Security.Cryptography;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "test-results", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class AudioTests
{
    private static void WritePcm16(string path, short[] samples, int rate, int channels)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + samples.Length * 2); bw.Write("WAVEfmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)channels); bw.Write(rate); bw.Write(rate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(samples.Length * 2);
        foreach (var s in samples) bw.Write(s);
    }

    [Fact]
    public void WAVを16kHzモノラルに読む_ステレオ48kHzも()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "a.wav");
        WritePcm16(p, Enumerable.Repeat((short)16384, 48000 * 2).ToArray(), 48000, 2); // 1 秒
        var s = Audio.ReadWav16kMono(p);
        Assert.Equal(16000, s.Length);
        Assert.All(s, x => Assert.Equal(0.5f, x, 3));
    }

    [Fact]
    public void 無音判定は最も大きい30ms区間のRMS()
    {
        Assert.Equal(0, Audio.PeakFrameRms(FakeRecorder.Silence(1)));
        var a = FakeRecorder.Silence(1);
        for (int i = 8000; i < 8480; i++) a[i] = 0.1f; // 30 ms だけ音がある
        Assert.True(Audio.PeakFrameRms(a) > 0.05);
    }

    [Fact]
    public void 同梱の音声素材が読める()
    {
        var dir = Scenario.ResolvePath(System.IO.Path.Combine(AppContext.BaseDirectory, "x.json"), "tests/fixtures/audio");
        var one = Audio.ReadWav16kMono(System.IO.Path.Combine(dir, "ja-one-sentence.wav"));
        var sil = Audio.ReadWav16kMono(System.IO.Path.Combine(dir, "silence.wav"));
        Assert.True(one.Length > Audio.SampleRate * 2);
        Assert.True(Audio.PeakFrameRms(one) >= new AppSettings().SilenceThreshold);
        Assert.True(Audio.PeakFrameRms(sil) < new AppSettings().SilenceThreshold);
    }
}

public class SettingsTests
{
    [Fact]
    public void 無ければ初期値で作る_トークキーは右Ctrl_モデルはlarge_v3_turbo()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "settings.json");
        var s = AppSettings.LoadOrCreate(p);
        Assert.True(File.Exists(p));
        Assert.Equal("RControlKey", s.TalkKey);
        Assert.Equal("large-v3-turbo", s.Model);
        Assert.Equal(0.3, s.MinPressSeconds);
        Assert.False(s.AutoStart);
    }

    [Fact]
    public void 保存した値を読み戻す_壊れていれば初期値と_bad()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "settings.json");
        new AppSettings { TalkKey = "RMenu" }.Save(p);
        Assert.Equal("RMenu", AppSettings.LoadOrCreate(p).TalkKey);
        File.WriteAllText(p, "{ broken");
        Assert.Equal("RControlKey", AppSettings.LoadOrCreate(p).TalkKey);
        Assert.True(File.Exists(p + ".bad"));
    }
}

public class ModelProvisionerTests
{
    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static ModelEntry Entry(byte[] body) =>
        new("t", "ggml-t.bin", "https://example.invalid/ggml-t.bin", Convert.ToHexStringLower(SHA256.HashData(body)), body.Length);

    [Fact]
    public async Task 無ければ取得してハッシュを確かめて置く_2回目は取得しない()
    {
        using var d = new TempDir();
        var body = "model-bytes"u8.ToArray();
        var h = new BytesHandler(body);
        var prov = new ModelProvisioner(d.Path, new HttpClient(h));
        var path = await prov.EnsureAsync(Entry(body), null, default);
        Assert.Equal(body, File.ReadAllBytes(path));
        await prov.EnsureAsync(Entry(body), null, default);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task ハッシュが合わない取得物は置かない()
    {
        using var d = new TempDir();
        var e = Entry("expected"u8.ToArray());
        var prov = new ModelProvisioner(d.Path, new HttpClient(new BytesHandler("tampered"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() => prov.EnsureAsync(e, null, default));
        Assert.False(File.Exists(prov.PathOf(e)));
    }

    [Fact]
    public async Task 手動で置いたモデルはハッシュが合えば取得せずに使う()
    {
        using var d = new TempDir();
        var body = "manual"u8.ToArray();
        var e = Entry(body);
        File.WriteAllBytes(System.IO.Path.Combine(d.Path, e.FileName), body);
        var h = new BytesHandler([]);
        var path = await new ModelProvisioner(d.Path, new HttpClient(h)).EnsureAsync(e, null, default);
        Assert.Equal(0, h.Calls);
        Assert.Equal(body, File.ReadAllBytes(path));
    }

    [Fact]
    public void 初期モデルの取得元はwhisper_cppの公式()
    {
        var e = ModelCatalog.Get(ModelCatalog.DefaultName);
        Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/", e.Url);
        Assert.Equal(64, e.Sha256.Length);
    }

    [Fact]
    public void カタログの全モデルは公式の取得元とハッシュとサイズを持つ()
    {
        Assert.Equal(ModelCatalog.Entries.Count, ModelCatalog.All.Count); // 名前の重複なし
        foreach (var e in ModelCatalog.Entries)
        {
            Assert.Matches("^[a-z0-9.-]+$", e.Name);
            Assert.Equal($"ggml-{e.Name}.bin", e.FileName);
            Assert.Equal($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{e.FileName}", e.Url);
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.True(e.Size > 10_000_000, e.Name);
        }
        Assert.Equal(ModelCatalog.Entries.Count, ModelCatalog.Entries.Select(e => e.Sha256).Distinct().Count());
    }

    [Fact]
    public void 小さい側と大きい側を選べて既定はlarge_v3_turbo()
    {
        Assert.Equal("large-v3-turbo", new AppSettings().Model);
        Assert.Contains(ModelCatalog.Entries, e => e.Name is "base" or "small");
        Assert.Contains(ModelCatalog.Entries, e => e.Name == "large-v3");
        var sizes = ModelCatalog.Entries.Select(e => e.Size).ToList();
        Assert.Equal(sizes.Order(), sizes); // 小さい順に並ぶ
    }

    [Theory]
    [InlineData("small", "small")]
    [InlineData("Small", "small")]
    [InlineData(" base ", "base")]
    [InlineData("ggml-large-v3.bin", "large-v3")]
    [InlineData("large-v3-turbo", "large-v3-turbo")]
    public void モデルは名前かファイル名で引ける(string input, string expected) =>
        Assert.Equal(expected, ModelCatalog.Get(input).Name);

    [Theory]
    [InlineData("whisper-small")]
    [InlineData("")]
    [InlineData(null)]
    public void 未知のモデルのエラー文に選べる名前の一覧が入る(string? input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ModelCatalog.Get(input));
        Assert.StartsWith($"未知のモデル: {input}", ex.Message);
        foreach (var e in ModelCatalog.Entries) Assert.Contains(e.Name, ex.Message);
    }
}

public class VerificationTests
{
    [Fact]
    public void 一致率は句読点と空白を無視する()
    {
        Assert.Equal(1.0, TextSimilarity.Ratio("今日の会議の資料を送ります。", "今日の会議の資料を送ります"));
        Assert.True(TextSimilarity.Ratio("今日の会議の資料を送ります", "今日の会議資料を送ります") > 0.9);
        Assert.True(TextSimilarity.Ratio("今日の会議の資料を送ります", "明日は晴れ") < 0.5);
    }

    private static VerifyResult Ok() => new()
    {
        Completed = true,
        Deliveries = [new() { Seq = 1, To = "textbox", Text = "こんにちは。", ReleaseToDeliverMs = 900 }],
        States = [new() { State = "準備中" }, new() { State = "録音中" }, new() { State = "処理中" }, new() { State = "貼り付け完了", Screenshot = "shots/04.png" }],
        Textbox = "こんにちは",
    };

    private static Expectation Exp() => new()
    {
        Deliveries = [new() { To = "textbox", Text = "こんにちは", MinSimilarity = 0.8 }],
        Textbox = new() { Text = "こんにちは" },
        States = ["準備中", "録音中", "処理中", "貼り付け完了"],
        Cancellations = [],
        MaxReleaseToDeliverMs = 1000,
        Screenshots = ["貼り付け完了"],
    };

    [Fact]
    public void 期待どおりなら失敗なし() => Assert.Empty(Evaluator.Check(Exp(), Ok()));

    [Fact]
    public void 状態の順序_時間_スクショ_届け先の違いを失敗にする()
    {
        var r = Ok();
        r.States = [new() { State = "録音中" }, new() { State = "準備中" }];
        r.Deliveries[0].To = "clipboard";
        r.Deliveries[0].ReleaseToDeliverMs = 5000;
        var fails = Evaluator.Check(Exp(), r);
        Assert.Contains(fails, f => f.Contains("状態"));
        Assert.Contains(fails, f => f.Contains("届け先"));
        Assert.Contains(fails, f => f.Contains("上限"));
        Assert.Contains(fails, f => f.Contains("スクリーンショット"));
    }

    [Fact]
    public void 期待したバックエンドで動いていなければ失敗にする()
    {
        var e = Exp();
        e.Runtime = "Cuda";
        var r = Ok();
        r.Runtime = "Cpu";
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("バックエンド Cpu"));
        r.Runtime = "Cuda";
        Assert.Empty(Evaluator.Check(e, r));
    }

    [Fact]
    public void 期待したモデルで動いていなければ失敗にする()
    {
        var e = Exp();
        e.Model = "base";
        var r = Ok();
        r.Model = "large-v3-turbo";
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("モデル large-v3-turbo"));
        r.Model = "base";
        Assert.Empty(Evaluator.Check(e, r));
    }

    [Fact]
    public void オーバーレイが前面になったら期待に書かなくても失敗にする()
    {
        var r = Ok();
        r.ForegroundSamples = 120;
        Assert.Empty(Evaluator.Check(Exp(), r));
        r.OverlayForegroundCount = 2;
        r.States[1].Foreground = "overlay";
        var fails = Evaluator.Check(new Expectation(), r);
        var f = Assert.Single(fails);
        Assert.Contains("前面", f);
        Assert.Contains("録音中", f);
    }

    [Fact]
    public void 台本の音声パスはリポジトリ直下からの相対でも解ける()
    {
        var scenario = Scenario.ResolvePath(System.IO.Path.Combine(AppContext.BaseDirectory, "x.json"), "tests/scenarios/junior-3.json");
        Assert.True(File.Exists(scenario));
        var sc = Scenario.Load(scenario);
        Assert.Equal("junior-3", sc.Name);
        Assert.True(File.Exists(Scenario.ResolvePath(scenario, sc.Actions.First(a => a.Do == "press").Audio!)));
    }
}
