using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class SettingsCaptureTests
{
    private const int White = unchecked((int)0xFFFFFFFF), Gray = unchecked((int)0xFFAAAAAA), Black = unchecked((int)0xFF000000);

    private static int[] Image(int w, int h, int fill = White) => Enumerable.Repeat(fill, w * h).ToArray();

    [Fact]
    public void 欄に暗い画素があれば値が写っている()
    {
        var px = Image(20, 10);
        Assert.False(SettingsCapture.HasInk(px, 20, 2, 2, 10, 5));
        px[4 * 20 + 6] = Black;
        Assert.True(SettingsCapture.HasInk(px, 20, 2, 2, 10, 5));
        Assert.False(SettingsCapture.HasInk(px, 20, 8, 2, 10, 5)); // 欄の外の画素は見ない
    }

    [Fact]
    public void 欄の地と枠の灰色は文字に数えない() => Assert.False(SettingsCapture.HasInk(Image(10, 10, Gray), 10, 0, 0, 10, 10));

    [Fact]
    public void 画の外にはみ出した欄は内側だけ見る()
    {
        var px = Image(10, 10);
        px[9 * 10 + 9] = Black;
        Assert.True(SettingsCapture.HasInk(px, 10, 5, 5, 100, 100));
        Assert.False(SettingsCapture.HasInk(px, 10, -5, -5, 8, 8));
    }

    [Fact]
    public void 値が写っていない選択式の項目を返す_値が空の項目と見えない欄は数えない()
    {
        var px = Image(40, 10);
        px[5 * 40 + 3] = Black; // talkKey の欄にだけ文字
        var blank = SettingsCapture.BlankFields(px, 40,
        [
            ("talkKey", "右Ctrl", 0, 0, 10, 10),
            ("engine", "Whisper(whisper.cpp、既定)", 10, 0, 10, 10),
            ("model", "", 20, 0, 10, 10),
            ("backend", "自動", 30, 0, 0, 0),
        ]);
        Assert.Equal(["engine"], blank);
    }

    [Fact]
    public void captureに写し方と空欄と書き足した項目を残す()
    {
        Assert.Equal("render", SettingsCapture.Describe("render", [], [], null));
        Assert.Equal("render (render left blank: talkKey,engine; from screen: talkKey,engine)",
            SettingsCapture.Describe("render", ["talkKey", "engine"], [], "from screen: talkKey,engine"));
        Assert.Equal("render (render left blank: talkKey; talkKey overlapped on screen; filled by verify: talkKey)",
            SettingsCapture.Describe("render", ["talkKey"], ["talkKey"], "talkKey overlapped on screen"));
    }

    [Fact]
    public void 設定画面のshotの期待_画面に出ている値()
    {
        var r = new VerifyResult { Completed = true };
        r.Shots.Add(new ShotRecord
        {
            Name = "開いた", State = "設定画面", Screenshot = "shots/01.png",
            Values = new() { ["talkKey"] = "RControlKey", ["engine"] = "whisper" },
        });
        Assert.Empty(Evaluator.Check(new Expectation { Shots = [new() { Name = "開いた", Values = new() { ["talkKey"] = "rcontrolkey" } }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "開いた", Values = new() { ["talkKey"] = "RMenu" } }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "開いた", Values = new() { ["model"] = "base" } }] }, r));
    }
}
