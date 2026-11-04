using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class SettingsSchemaTests
{
    [Fact]
    public void 設定ファイルの全項目が画面に名前と説明付きで並ぶ()
    {
        var keys = SettingsSchema.Items.Select(i => i.Key).ToList();
        Assert.Equal(["talkKey", "recordMode", "startKey", "stopKey", "engine", "model","backend", "preferSpeed", "delivery", "minPressSeconds", "silenceThreshold", "showInterim", "removeFillers", "fillers", "keywords", "fetchCudaRuntime", "autoStart"], keys);
        // settings.json に書かれるキーと一致する(画面に無い設定項目を作らない)
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettings.Json);
        var fileKeys = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(fileKeys.OrderBy(x => x), keys.OrderBy(x => x));
        Assert.All(SettingsSchema.Items, i => { Assert.NotEmpty(i.Label); Assert.NotEmpty(i.Description); });
    }

    [Fact]
    public void エンジンの説明は_用意できないとき押せばオーバーレイにも理由と直し方が出ると書く()
    {
        var d = SettingsSchema.Items.Single(i => i.Key == "engine").Description;
        Assert.Contains("トレイに出します", d);
        Assert.Contains("オーバーレイにも理由と直し方が出ます", d);
    }

    [Theory]
    [InlineData("model", "モデル", "Whisper")]
    [InlineData("preferSpeed", "速さを優先する", "Whisper")]
    [InlineData("fetchCudaRuntime", "CUDA を取得する", "Whisper")]
    public void 片方のエンジンでだけ効く項目は名前に括弧を付けず_説明の先頭で同じ形に示す(string key, string label, string engine)
    {
        var item = SettingsSchema.Get(key);
        Assert.Equal(label, item.Label);
        Assert.StartsWith($"エンジンが {engine} のときだけ使います。", item.Description);
    }

    [Fact]
    public void トークキーは押している間だけ録音のときだけ_開始キーと終了キーはどちらでも効くと説明の先頭で示す()
    {
        Assert.StartsWith("録音のしかたが「押している間だけ録音」のときだけ使います。", SettingsSchema.Get("talkKey").Description);
        Assert.StartsWith("どちらの録音のしかたでも効きます。", SettingsSchema.Get("startKey").Description);
        Assert.StartsWith("どちらの録音のしかたでも効きます。", SettingsSchema.Get("stopKey").Description);
        Assert.Contains("トークキーの録音に戻ります", SettingsSchema.Get("stopKey").Description);
    }

    [Fact]
    public void 組み合わせの説明と読めない値の知らせはコードが受け付ける修飾と同じ()
    {
        // KeyChord.TryParse は文字・数字・Space を Ctrl・Alt・Win のどれかとの組み合わせで受け付ける
        Assert.True(KeyChord.TryParse("Win+A", out _));
        Assert.Contains("Ctrl・Alt・Win のどれか", SettingsSchema.Get("startKey").Description);
        Assert.DoesNotContain("Ctrl か Alt", SettingsSchema.Get("startKey").Description);
        Assert.Contains("Ctrl・Alt・Win のどれか", SettingsSchema.Apply(new AppSettings(), "startKey", "Space"));
        KeyChord.ParseOr("Space", KeyChord.DefaultStart, "startKey", out var warn);
        Assert.Contains("Ctrl・Alt・Win のどれか", warn);
    }

    [Fact]
    public void カスタム語彙はどちらのエンジンでも効き_読みはHaasのときだけと同じ形で示す()
    {
        var item = SettingsSchema.Get("keywords");
        Assert.Equal("カスタム語彙", item.Label);
        Assert.DoesNotContain("エンジンが Haas のときだけ使います。固有", item.Description);
        Assert.Contains("どちらのエンジンでも効きます", item.Description);
        Assert.Contains("読みが効くのはエンジンが Haas のときで、Whisper は表記だけを使います", item.Description);
    }

    [Fact]
    public void エンジン限定の示し方は1つだけ_名前の括弧や説明の末尾では示さない()
    {
        foreach (var i in SettingsSchema.Items)
        {
            Assert.DoesNotContain("(Haas)", i.Label);
            Assert.DoesNotContain("(Whisper)", i.Label);
            Assert.DoesNotContain("では使いません", i.Description);
            int at = i.Description.IndexOf("のときだけ使います", StringComparison.Ordinal);
            // 録音のしかたで効かない項目も同じ形(説明の先頭)で示す(BLK-owner-20261103-2251-1 で「録音のしかたが 」を足した)
            Assert.True(at < 0 || i.Description.StartsWith("エンジンが ") || i.Description.StartsWith("録音のしかたが「"),$"{i.Key}: 説明の途中で限定を示している");
        }
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
        Assert.Null(SettingsSchema.Apply(s, "preferSpeed", value));
        Assert.Equal(expected, s.PreferSpeed);
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
