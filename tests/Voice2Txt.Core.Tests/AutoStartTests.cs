using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class AutoStartTests
{
    private sealed class FakeRun : IAutoStartRegistry
    {
        public string? Value;
        public int Writes;
        public string? Registered() => Value;
        public void Register(string command) { Value = command; Writes++; }
        public void Unregister() { Value = null; Writes++; }
    }

    private const string Exe = @"C:\Apps\Voice2Txt\Voice2Txt.exe";

    [Fact]
    public void 初期値はオフで_登録しない()
    {
        var run = new FakeRun();
        Assert.False(AutoStart.Sync(new AppSettings().AutoStart, Exe, run));
        Assert.Null(run.Value);
    }

    [Fact]
    public void オンなら登録_オフなら解除_一致していれば触らない()
    {
        var run = new FakeRun();
        Assert.True(AutoStart.Sync(true, Exe, run));
        Assert.Equal($"\"{Exe}\"", run.Value);
        Assert.False(AutoStart.Sync(true, Exe, run));
        Assert.Equal(1, run.Writes);
        Assert.True(AutoStart.Sync(false, Exe, run));
        Assert.Null(run.Value);
    }

    [Fact]
    public void exeの場所が変わっていたら登録し直す()
    {
        var run = new FakeRun { Value = "\"D:\\old\\Voice2Txt.exe\"" };
        Assert.True(AutoStart.Sync(true, Exe, run));
        Assert.Equal($"\"{Exe}\"", run.Value);
    }

    [Fact]
    public void メニューで切り替えると設定に保存され_登録と一致する()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        var s = AppSettings.LoadOrCreate(path);
        var run = new FakeRun();
        Assert.True(AutoStart.Toggle(s, path, Exe, run));
        Assert.True(AppSettings.LoadOrCreate(path).AutoStart);
        Assert.NotNull(run.Value);
        Assert.False(AutoStart.Toggle(s, path, Exe, run));
        Assert.False(AppSettings.LoadOrCreate(path).AutoStart);
        Assert.Null(run.Value);
    }
}
