using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

/// <summary>動いているアプリの版: exe の版は VERSION から入り、画面にはタグの形(v{major}.{minor})で出す。</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("0.4.0", "v0.4")]
    [InlineData("1.0.0", "v1.0")]
    [InlineData("0.4.1", "v0.4.1")]
    [InlineData("0.4.0\r\n", "v0.4")]
    [InlineData("0.4.0+abc123", "v0.4")]
    [InlineData("", "v?")]
    [InlineData("abc", "v?")]
    public void 版はタグの形で出す(string version, string tag) => Assert.Equal(tag, AppVersion.Tag(version));

    [Fact]
    public void 組み込まれた版はリポジトリのVERSIONと一致する()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var file = File.ReadAllText(Path.Combine(dir!.FullName, "VERSION")).Trim();
        Assert.Equal(file, AppVersion.Current);
        Assert.Equal($"Voice2Txt {AppVersion.Tag(file)}", AppVersion.Label);
    }

    [Fact]
    public void トレイのツールチップは版で始まり_63文字に収まる()
    {
        Assert.StartsWith(AppVersion.Label + " — 待機中", AppVersion.TrayText("待機中(モデル large-v3-turbo)"));
        Assert.Equal(63, AppVersion.TrayText(new string('あ', 100)).Length);
    }

    [Fact]
    public void 検証の期待_版がresultとツールチップに出ていなければfailed()
    {
        var e = new Expectation { Version = "v0.4" };
        Assert.Empty(Evaluator.Check(e, new VerifyResult { Completed = true, Version = "v0.4", TrayTooltip = "Voice2Txt v0.4 — 待機中" }));
        Assert.Single(Evaluator.Check(e, new VerifyResult { Completed = true, Version = "v0.3", TrayTooltip = "Voice2Txt v0.4 — 待機中" }));
        Assert.Single(Evaluator.Check(e, new VerifyResult { Completed = true, Version = "v0.4", TrayTooltip = "Voice2Txt — 待機中" }));
    }
}
