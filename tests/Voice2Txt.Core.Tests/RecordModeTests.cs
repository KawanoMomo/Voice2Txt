using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class RecordModeTests
{
    // 仮想キーコード
    private const int LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, LShift = 0xA0, Space = 0x20, C = 0x43, S = 0x53, Enter = 0x0D;

    private static KeyChord Chord(string s) { Assert.True(KeyChord.TryParse(s, out var c), s); return c; }

    [Theory]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("alt + ctrl + space", "Ctrl+Alt+Space")]
    [InlineData("Control+Shift+r", "Ctrl+Shift+R")]
    [InlineData("Ctrl+Alt+F9", "Ctrl+Alt+F9")]
    [InlineData("Win+Alt+5", "Alt+Win+5")]
    [InlineData("Pause", "Pause")]
    [InlineData("F10", "F10")]
    [InlineData("Ctrl+Alt+PageDown", "Ctrl+Alt+PageDown")]
    public void 組み合わせを読み_設定ファイルの形で書く(string text, string expected) => Assert.Equal(expected, Chord(text).ToString());

    // ふつうの入力とぶつかる組み合わせ・読めない名前は読まない
    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("R")]
    [InlineData("Shift+R")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+R")]
    [InlineData("Ctrl+Alt+Foo")]
    [InlineData("Hyper+R")]
    [InlineData("Ctrl+Alt+F25")]
    public void ぶつかる組み合わせや読めない名前は読まない(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void 読めない値は既定にして知らせる()
    {
        var c = KeyChord.ParseOr("Space", KeyChord.DefaultStart, "startKey", out var warn);
        Assert.Equal("Ctrl+Alt+Space", c.ToString());
        Assert.Contains("startKey「Space」", warn);
        KeyChord.ParseOr("Ctrl+Alt+R", KeyChord.DefaultStart, "startKey", out warn);
        Assert.Null(warn);
    }

    [Fact]
    public void 開始キーで始まり_他のキーは素通しして取り消さず_終了キーで止まる()
    {
        var f = new ToggleKeyFilter(Chord("Ctrl+Alt+Space"), Chord("Ctrl+Alt+Space"));
        Assert.Equal(KeyVerdict.Pass.Swallow, f.OnKey(LCtrl, true).Swallow);
        Assert.False(f.OnKey(LAlt, true).Swallow);
        var start = f.OnKey(Space, true);
        Assert.True(start.Swallow);
        Assert.Equal(TalkKeySignal.TalkDown, start.Signal);
        Assert.Empty(start.InjectDown);
        Assert.Equal(new KeyVerdict(true, TalkKeySignal.None, []).Signal, f.OnKey(Space, true).Signal); // リピート
        Assert.True(f.OnKey(Space, false).Swallow);
        Assert.False(f.OnKey(LAlt, false).Swallow);
        Assert.False(f.OnKey(LCtrl, false).Swallow);
        Assert.True(f.Recording);

        // 録音中の他のキー・ショートカットはアプリへ渡り、録音は取り消さない
        foreach (var (vk, down) in new[] { (C, true), (C, false), (LCtrl, true), (S, true), (S, false), (LCtrl, false), (Space, true), (Space, false), (RCtrl, true), (RCtrl, false), (Enter, true), (Enter, false) })
        {
            var v = f.OnKey(vk, down);
            Assert.False(v.Swallow, $"{vk:X2} {down}");
            Assert.Equal(TalkKeySignal.None, v.Signal);
        }

        f.OnKey(LCtrl, true); f.OnKey(LAlt, true);
        var stop = f.OnKey(Space, true);
        Assert.True(stop.Swallow);
        Assert.Equal(TalkKeySignal.TalkUp, stop.Signal);
        Assert.False(f.Recording);
    }

    [Fact]
    public void 修飾が過不足なく一致したときだけ()
    {
        var f = new ToggleKeyFilter(Chord("Ctrl+Alt+Space"), Chord("Ctrl+Alt+Space"));
        f.OnKey(LCtrl, true); f.OnKey(LAlt, true); f.OnKey(LShift, true);
        Assert.Equal(KeyVerdict.Pass, f.OnKey(Space, true)); // Ctrl+Alt+Shift+Space は違う
        f.OnKey(Space, false); f.OnKey(LShift, false); f.OnKey(LAlt, false);
        Assert.Equal(KeyVerdict.Pass, f.OnKey(Space, true)); // Ctrl+Space も違う
        f.OnKey(Space, false); f.OnKey(LCtrl, false);
        Assert.Equal(KeyVerdict.Pass, f.OnKey(Space, true)); // Space だけはふつうの入力
        Assert.False(f.Recording);
        // 右の Ctrl でも Ctrl
        f.OnKey(Space, false); f.OnKey(RCtrl, true); f.OnKey(LAlt, true);
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(Space, true).Signal);
    }

    [Fact]
    public void 開始キーと終了キーが違えば_録音中の開始キーは何もせずアプリにも渡さない()
    {
        var f = new ToggleKeyFilter(Chord("Ctrl+Alt+R"), Chord("Ctrl+Alt+S"));
        f.OnKey(LCtrl, true); f.OnKey(LAlt, true);
        Assert.Equal(KeyVerdict.Pass, f.OnKey(S, true)); // 録音していない間の終了キーはふつうのショートカット
        f.OnKey(S, false);
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(0x52, true).Signal);
        f.OnKey(0x52, false);
        var again = f.OnKey(0x52, true);
        Assert.True(again.Swallow);
        Assert.Equal(TalkKeySignal.None, again.Signal);
        f.OnKey(0x52, false);
        Assert.Equal(TalkKeySignal.TalkUp, f.OnKey(S, true).Signal);
    }

    [Fact]
    public void 録音が始まらなかったら次の開始キーをもう一度開始として受ける()
    {
        var f = new ToggleKeyFilter(Chord("Ctrl+Alt+Space"), Chord("Ctrl+Alt+Space"));
        f.OnKey(LCtrl, true); f.OnKey(LAlt, true);
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(Space, true).Signal);
        f.OnKey(Space, false);
        f.Reset();
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(Space, true).Signal);
    }

    [Theory]
    [InlineData("toggle", "toggle")]
    [InlineData("押さない録音", "toggle")]
    [InlineData("HOLD", "hold")]
    [InlineData("", "hold")]
    public void 録音のしかたを読む(string v, string expected) => Assert.Equal(expected, RecordModes.Normalize(v));

    [Fact]
    public void 読めない録音のしかたは押している間だけにして知らせる()
    {
        Assert.False(RecordModes.IsToggle("tggle"));
        Assert.Contains("recordMode「tggle」", RecordModes.InvalidWarning("tggle"));
        Assert.Null(RecordModes.InvalidWarning(null));
    }

    [Fact]
    public void 設定画面から録音のしかたと開始キー終了キーを選べる()
    {
        var s = new AppSettings();
        Assert.Equal("hold", SettingsSchema.Read(s, "recordMode"));
        Assert.Equal("Ctrl+Alt+Space", SettingsSchema.Read(s, "startKey"));
        Assert.Null(SettingsSchema.Apply(s, "recordMode", "押さない録音"));
        Assert.Equal("toggle", s.RecordMode);
        Assert.Null(SettingsSchema.Apply(s, "startKey", "Ctrl+Alt+R"));
        Assert.Null(SettingsSchema.Apply(s, "stopKey", "Ctrl+Alt+Space(既定)"));
        Assert.Null(SettingsSchema.Apply(s, "stopKey", "alt+ctrl+f12")); // 選択肢に無くても読める組み合わせなら設定ファイルの形で
        Assert.Equal(("Ctrl+Alt+R", "Ctrl+Alt+F12"), (s.StartKey, s.StopKey));
        Assert.NotNull(SettingsSchema.Apply(s, "startKey", "Space"));
        Assert.NotNull(SettingsSchema.Apply(s, "recordMode", "ずっと"));
        Assert.Equal("Ctrl+Alt+R", s.StartKey);
        var copy = new AppSettings(); copy.CopyFrom(s);
        Assert.Equal(("toggle", "Ctrl+Alt+R", "Ctrl+Alt+F12"), (copy.RecordMode, copy.StartKey, copy.StopKey));
    }

    [Fact]
    public void 押さない録音の録音中の案内は終了キーで入力()
    {
        var v = new OverlayView(OverlayState.Recording);
        Assert.Equal("録音中  Ctrl+Alt+Space で入力", v.Text("Ctrl+Alt+Space", toggle: true));
        Assert.Equal("録音中  右Ctrl を離すと入力", v.Text("右Ctrl"));
        Assert.Equal(["録音中", "", "Ctrl+Alt+Space", "で入力"], v.Parts("Ctrl+Alt+Space", toggle: true).Select(p => p.Text));
    }

    [Fact]
    public async Task 開始キーから終了キーまでが1つの発話になる()
    {
        await using var r = new Rig();
        r.Engine.Text = _ => "長いファイルを見ながら話しました。";
        Assert.True(r.Ptt.OnToggleStart());
        r.Clock.NowMs += 20_000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("長いファイルを見ながら話しました。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task モデル準備中の開始キーは録音せず知らせを出して終える()
    {
        await using var r = new Rig(new PttOptions(ModelNoticeMs: 50), modelReady: false);
        Assert.False(r.Ptt.OnToggleStart());
        Assert.Equal(OverlayState.ModelPreparing, r.Ptt.CurrentView.State);
        Assert.Contains(r.Reports, x => x.Reason == CancelReason.ModelNotReady);
        Assert.True(await r.Ptt.WaitIdleAsync(TimeSpan.FromSeconds(1))); // 終了キーを待って押下中のままにならない
    }
}
