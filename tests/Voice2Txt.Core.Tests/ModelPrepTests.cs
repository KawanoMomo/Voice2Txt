using System.Diagnostics;
using System.Security.Cryptography;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class ModelPrepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v2t-prep-" + Guid.NewGuid().ToString("N"));

    public ModelPrepTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("ダウンロード 30%", "ダウンロード", 30)]
    [InlineData("検証 100%", "検証", 100)]
    [InlineData("CUDA の準備中 ダウンロード 5%", "CUDA の準備中 ダウンロード", 5)]
    [InlineData("読み込み", "読み込み", null)]
    [InlineData("暖機", "暖機", null)]
    public void 準備の文を段階と進み具合に分ける(string detail, string stage, int? pct)
    {
        Assert.Equal((stage, pct), ModelPrepProgress.Parse(detail));
    }

    [Fact]
    public void トレイの文言は常駐時と同じ形()
    {
        Assert.Equal("モデル準備中 ダウンロード 30%", ModelPrepProgress.TrayStatus("ダウンロード 30%"));
        Assert.Equal("モデル準備中", ModelPrepProgress.TrayStatus(null));
    }

    [Fact]
    public void 記録は段階が変わるか10パーセントの刻みを跨いだときだけ()
    {
        var t = new ModelPrepTracker();
        var kept = Enumerable.Range(0, 101).Select(i => $"ダウンロード {i}%")
            .Concat(Enumerable.Range(0, 101).Select(i => $"検証 {i}%"))
            .Append("読み込み").Append("暖機")
            .Where(t.Accept).ToList();
        Assert.Equal(11 + 11 + 2, kept.Count);
        Assert.Equal(["ダウンロード 0%", "ダウンロード 10%"], kept.Take(2));
        Assert.Contains("ダウンロード 100%", kept);
        Assert.Equal(["読み込み", "暖機"], kept.TakeLast(2));
        Assert.False(t.Accept("暖機"));
        t.Reset();
        Assert.True(t.Accept("暖機"));
    }

    [Fact]
    public async Task 手元の取得元から本物と同じ経路で取得し_時間をかけて進み具合を出す()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var bytes = new byte[3 << 20];
        new Random(1).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(src, "ggml-fake.bin"), bytes);
        var entry = new ModelEntry("fake", "ggml-fake.bin", "https://example.invalid/x/ggml-fake.bin",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

        var dst = Path.Combine(_dir, "dst");
        var prov = new ModelProvisioner(dst, new HttpClient(new LocalModelSourceHandler(src, 600)));
        var seen = new List<(string Phase, double Ratio)>();
        var sw = Stopwatch.StartNew();
        var path = await prov.EnsureAsync(entry, new InlineProgress<(string, double)>(seen.Add), CancellationToken.None);

        Assert.True(sw.ElapsedMilliseconds >= 450, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.All(seen, p => Assert.Equal("ダウンロード", p.Phase));
        Assert.True(seen.Count >= 20, $"進み具合 {seen.Count} 回");
        Assert.Equal(1.0, seen[^1].Ratio, 3);
    }

    [Fact]
    public void その場で渡す進み具合は報告した順に届く()
    {
        var got = new List<int>();
        IProgress<int> p = new InlineProgress<int>(got.Add);
        for (int i = 0; i < 50; i++) p.Report(i);
        got.Add(-1); // 次の段階(報告の直後に呼ぶ)
        Assert.Equal(Enumerable.Range(0, 50).Append(-1), got);
    }

    [Fact]
    public async Task 手元に無いモデルは取得できない()
    {
        var entry = new ModelEntry("none", "ggml-none.bin", "https://example.invalid/ggml-none.bin", new string('0', 64), 1);
        var prov = new ModelProvisioner(Path.Combine(_dir, "dst"), new HttpClient(new LocalModelSourceHandler(_dir, 10)));
        await Assert.ThrowsAsync<HttpRequestException>(() => prov.EnsureAsync(entry, null, CancellationToken.None));
    }

    [Fact]
    public void 期待した準備の段階が順に記録されていなければ赤()
    {
        var e = new Expectation { ModelPrep = ["ダウンロード", "読み込み", "暖機"] };
        ModelPrepRecord R(string s) => new() { Stage = s };
        Assert.Empty(Evaluator.Check(e, new VerifyResult { Completed = true, ModelPrep = [R("ダウンロード"), R("ダウンロード"), R("読み込み"), R("暖機")] }));
        var fails = Evaluator.Check(e, new VerifyResult { Completed = true, ModelPrep = [R("読み込み"), R("暖機")] });
        Assert.Single(fails);
        Assert.Contains("ダウンロード", fails[0]);
    }
}
