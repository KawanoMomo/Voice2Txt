using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class SettingsSchemaTests
{
    [Fact]
    public void 設定ファイルの全項目が画面に名前と説明付きで並ぶ()
    {
        var keys = SettingsSchema.Items.Select(i => i.Key).ToList();
        Assert.Equal(["talkKey", "model", "minPressSeconds", "silenceThreshold", "showInterim", "removeFillers", "fillers", "autoStart"], keys);
        // settings.json に書かれるキーと一致する(画面に無い設定項目を作らない)
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettings.Json);
        var fileKeys = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(fileKeys.OrderBy(x => x), keys.OrderBy(x => x));
        Assert.All(SettingsSchema.Items, i => { Assert.NotEmpty(i.Label); Assert.NotEmpty(i.Description); });
    }

    [Fact]
    public void トークキーは画面の名前でも設定の名前でも選べる()
    {
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "talkKey", "右Alt"));
        Assert.Equal("RMenu", s.TalkKey);
        Assert.Null(SettingsSchema.Apply(s, "talkKey", "RShiftKey"));
        Assert.Equal("RShiftKey", s.TalkKey);
        Assert.Equal("右Alt", SettingsSchema.TalkKeyLabel("RMenu"));
        Assert.Equal("右Ctrl", SettingsSchema.TalkKeyLabel("RControlKey"));
    }

    [Fact]
    public void 選択肢に無いトークキーは選べないが_手で書いた今の値は残せる()
    {
        var s = new AppSettings();
        var err = SettingsSchema.Apply(s, "talkKey", "A");
        Assert.NotNull(err);
        Assert.Contains("右Alt", err);
        Assert.Equal("RControlKey", s.TalkKey);
        s.TalkKey = "F13";
        Assert.Null(SettingsSchema.Apply(s, "talkKey", "F13"));
        Assert.Equal("F13", s.TalkKey);
    }

    [Fact]
    public void モデルはカタログの名前から選び_既定と大きさが分かる()
    {
        Assert.Equal(ModelCatalog.Entries.Select(e => e.Name), SettingsSchema.ModelChoices.Select(c => c.Value));
        Assert.Contains("既定", SettingsSchema.ModelChoices.Single(c => c.Value == ModelCatalog.DefaultName).Label);
        Assert.Contains("GB", SettingsSchema.ModelChoices.Single(c => c.Value == "large-v3").Label);
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "model", "small"));
        Assert.Equal("small", s.Model);
        Assert.NotNull(SettingsSchema.Apply(s, "model", "huge"));
        Assert.Equal("small", s.Model);
    }

    [Theory]
    [InlineData("minPressSeconds", "0.5", null)]
    [InlineData("minPressSeconds", "5", "範囲")]
    [InlineData("minPressSeconds", "abc", "数ではありません")]
    [InlineData("silenceThreshold", "0.005", null)]
    [InlineData("silenceThreshold", "0.5", "範囲")]
    public void 数は範囲を確かめて移す(string key, string value, string? error)
    {
        var s = new AppSettings();
        var before = SettingsSchema.Read(s, key);
        var err = SettingsSchema.Apply(s, key, value);
        if (error is null) { Assert.Null(err); Assert.Equal(value, SettingsSchema.Read(s, key)); }
        else { Assert.Contains(error, err); Assert.Equal(before, SettingsSchema.Read(s, key)); }
    }

    [Theory]
    [InlineData("オン", true)]
    [InlineData("オフ", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void オンオフは画面の言葉でも真偽でも移せる(string value, bool expected)
    {
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "autoStart", value));
        Assert.Equal(expected, s.AutoStart);
        Assert.Null(SettingsSchema.Apply(s, "removeFillers", value));
        Assert.Equal(expected, s.RemoveFillers);
        Assert.Null(SettingsSchema.Apply(s, "showInterim", value));
        Assert.Equal(expected, s.ShowInterim);
        Assert.NotNull(SettingsSchema.Apply(s, "autoStart", "たぶん"));
    }

    [Fact]
    public void 言い淀みの語は読点か空白で区切る()
    {
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "fillers", "えっと、あの  なんか,えっと"));
        Assert.Equal(["えっと", "あの", "なんか"], s.Fillers);
        Assert.Equal("えっと、あの、なんか", SettingsSchema.Read(s, "fillers"));
    }

    [Fact]
    public void 読んだ値をそのまま移しても設定は変わらない()
    {
        var s = new AppSettings { TalkKey = "RMenu", Model = "base", MinPressSeconds = 0.4, SilenceThreshold = 0.004, AutoStart = true, RemoveFillers = false };
        var copy = SettingsSchema.Clone(s);
        foreach (var i in SettingsSchema.Items) Assert.Null(SettingsSchema.Apply(copy, i.Key, SettingsSchema.Read(s, i.Key)));
        Assert.Empty(SettingsSchema.Changed(s, copy));
        copy.TalkKey = "RShiftKey";
        Assert.Equal(["talkKey"], SettingsSchema.Changed(s, copy));
        Assert.Equal("RMenu", s.TalkKey); // 複製を変えても元は変わらない
    }

    [Fact]
    public void 写すと全項目が揃う()
    {
        var a = new AppSettings();
        var b = new AppSettings { TalkKey = "RMenu", Model = "base", MinPressSeconds = 0.4, SilenceThreshold = 0.004, AutoStart = true, RemoveFillers = false, Fillers = ["なんか"], ShowInterim = false };
        a.CopyFrom(b);
        Assert.Empty(SettingsSchema.Changed(a, b));
    }

    [Fact]
    public void 台本の_savedSettings_と保存の失敗を確かめる()
    {
        var e = new Expectation { SavedSettings = new() { ["talkKey"] = "RMenu" }, SettingsErrors = [] };
        var r = new VerifyResult { Completed = true };
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("保存されていない"));
        r.SavedSettings = new AppSettings { TalkKey = "RShiftKey" };
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("talkKey=RShiftKey"));
        r.SavedSettings.TalkKey = "RMenu";
        Assert.Empty(Evaluator.Check(e, r));
        r.SettingsErrors.Add("トークキー「A」は選べません");
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("保存の失敗"));
    }
}
