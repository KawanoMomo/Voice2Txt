using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class LegacyNameTests
{
    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void 製品名はVoice2Txt_旧名は接頭辞付き()
    {
        Assert.Equal("Voice2Txt", AppVersion.ProductName);
        Assert.Equal("FF" + "_Voice2Txt", LegacyName.Name);
        Assert.EndsWith(Path.DirectorySeparatorChar + "Voice2Txt", AppSettings.DefaultDirectory);
        Assert.EndsWith(Path.DirectorySeparatorChar + "Voice2Txt", AppSettings.LocalDirectory);
    }

    [Fact]
    public void 旧フォルダしか無ければ_中身ごと新しい名前へ移す()
    {
        using var d = new TempDir();
        var old = Path.Combine(d.Path, LegacyName.Name);
        var neu = Path.Combine(d.Path, AppVersion.ProductName);
        Put(Path.Combine(old, "settings.json"), "{\"talkKey\":\"RMenu\"}");
        Put(Path.Combine(old, "models", "ggml-tiny.bin"), "model");
        Put(Path.Combine(old, "logs", "app.log"), "log");

        var r = LegacyName.MigrateFolder(old, neu);

        Assert.Equal(new LegacyName.Result(3, 0, 0), r);
        Assert.False(Directory.Exists(old));
        Assert.Equal("{\"talkKey\":\"RMenu\"}", File.ReadAllText(Path.Combine(neu, "settings.json")));
        Assert.True(File.Exists(Path.Combine(neu, "models", "ggml-tiny.bin")));
        Assert.Equal("RMenu", AppSettings.LoadOrCreate(Path.Combine(neu, "settings.json")).TalkKey);
    }

    [Fact]
    public void 新しいフォルダが既にあれば_無いものだけ移し_あるものは新しい方を残す()
    {
        using var d = new TempDir();
        var old = Path.Combine(d.Path, LegacyName.Name);
        var neu = Path.Combine(d.Path, AppVersion.ProductName);
        Put(Path.Combine(old, "settings.json"), "old");
        Put(Path.Combine(old, "models", "ggml-tiny.bin"), "model");
        Put(Path.Combine(neu, "settings.json"), "new-settings");

        var r = LegacyName.MigrateFolder(old, neu);

        Assert.Equal(new LegacyName.Result(1, 1, 0), r);
        Assert.Equal("new-settings", File.ReadAllText(Path.Combine(neu, "settings.json")));
        Assert.True(File.Exists(Path.Combine(neu, "models", "ggml-tiny.bin")));
        Assert.True(File.Exists(Path.Combine(old, "settings.json"))); // 消さずに残す
        Assert.False(Directory.Exists(Path.Combine(old, "models")));   // 空になったフォルダは消す
    }

    [Fact]
    public void 同じ大きさと更新時刻のファイルは同じものとして旧い方を消す()
    {
        using var d = new TempDir();
        var old = Path.Combine(d.Path, LegacyName.Name);
        var neu = Path.Combine(d.Path, AppVersion.ProductName);
        var a = Path.Combine(old, "models", "ggml-tiny.bin");
        var b = Path.Combine(neu, "models", "ggml-tiny.bin");
        Put(a, "model"); Put(b, "model");
        var t = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, t); File.SetLastWriteTimeUtc(b, t);

        var r = LegacyName.MigrateFolder(old, neu);

        Assert.Equal(new LegacyName.Result(1, 0, 0), r);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void 旧フォルダが無ければ何もしない()
    {
        using var d = new TempDir();
        var r = LegacyName.MigrateFolder(Path.Combine(d.Path, LegacyName.Name), Path.Combine(d.Path, AppVersion.ProductName));
        Assert.False(r.Any);
        Assert.False(Directory.Exists(Path.Combine(d.Path, AppVersion.ProductName)));
    }

    [Fact]
    public void 使用中で移せないファイルは旧フォルダに残し_他は移す()
    {
        using var d = new TempDir();
        var old = Path.Combine(d.Path, LegacyName.Name);
        var neu = Path.Combine(d.Path, AppVersion.ProductName);
        Put(Path.Combine(old, "settings.json"), "s");
        Put(Path.Combine(old, "logs", "app.log"), "log");
        using (File.Open(Path.Combine(old, "logs", "app.log"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = LegacyName.MigrateFolder(old, neu);
            Assert.Equal(new LegacyName.Result(1, 0, 1), r);
        }
        Assert.True(File.Exists(Path.Combine(neu, "settings.json")));
        Assert.True(File.Exists(Path.Combine(old, "logs", "app.log")));
    }

    private sealed class FakeRun : IAutoStartRegistry
    {
        public string? Value;
        public string? Registered() => Value;
        public void Register(string command) => Value = command;
        public void Unregister() => Value = null;
    }

    [Fact]
    public void 旧名の自動起動は外し_設定どおり新しい名前で登録し直す()
    {
        var legacy = new FakeRun { Value = "\"C:\\Apps\\old\\old.exe\"" };
        var current = new FakeRun();
        Assert.True(LegacyName.RemoveAutoStart(legacy));
        Assert.Null(legacy.Value);
        Assert.False(LegacyName.RemoveAutoStart(legacy));
        Assert.True(AutoStart.Sync(true, @"C:\Apps\Voice2Txt\Voice2Txt.exe", current));
        Assert.Equal("\"C:\\Apps\\Voice2Txt\\Voice2Txt.exe\"", current.Value);
    }
}
