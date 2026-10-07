using Voice2Txt.Core;
using Voice2Txt.Core.Verification;
using Xunit;

namespace Voice2Txt.Core.Tests;

public class OverlayLayoutTests
{
    private const int RowW = 230, RowH = 36, LineH = 19;

    private static (int X, int Y) HintRowOnScreen(int interimW, int interimH, int areaWidth = 1920)
    {
        var l = OverlayLayout.Of(RowW, RowH, interimW, interimH);
        var (left, top) = l.Place(0, areaWidth, 1040, RowW);
        return (left + l.RowLeft, top + l.RowTop);
    }

    [Fact]
    public void 案内の行は窓の一番下にあり途中経過はその上に積む()
    {
        foreach (int lines in new[] { 1, 2, 3 })
        {
            var l = OverlayLayout.Of(RowW, RowH, 520, LineH * lines);
            Assert.Equal(l.Height, l.RowTop + RowH);
            Assert.True(l.InterimTop + LineH * lines <= l.RowTop, "途中経過の欄が案内の行に重なる");
            Assert.True(l.InterimTop > 0);
        }
        var none = OverlayLayout.Of(RowW, RowH, 0, 0);
        Assert.Equal((RowH, 0, OverlayLayout.PadX, RowW + 2 * OverlayLayout.PadX), (none.Height, none.RowTop, none.RowLeft, none.Width));
    }

    [Fact]
    public void 途中経過の行数と幅が変わっても案内の行は画面上で動かない()
    {
        var without = HintRowOnScreen(0, 0);
        Assert.Equal(without, HintRowOnScreen(300, LineH));
        Assert.Equal(without, HintRowOnScreen(301, LineH));
        Assert.Equal(without, HintRowOnScreen(520, LineH * 3));
        Assert.Equal(HintRowOnScreen(0, 0, 1366), HintRowOnScreen(519, LineH * 2, 1366));
    }

    [Fact]
    public void 窓は作業領域の下端から一定の距離に置く()
    {
        foreach (int h in new[] { 0, LineH, LineH * 3 })
        {
            var l = OverlayLayout.Of(RowW, RowH, 520, h);
            var (_, top) = l.Place(0, 1920, 1040, RowW);
            Assert.Equal(1040 - OverlayLayout.BottomMargin, top + l.Height);
        }
    }

    [Fact]
    public void 台本の期待で案内の行の位置と途中経過の行数を比べる()
    {
        var r = new VerifyResult
        {
            Completed = true,
            Shots =
            [
                new() { Name = "1行", State = "録音中", InterimLines = 1, HintRowX = 845, HintRowY = 986, Screenshot = "shots/a.png" },
                new() { Name = "3行", State = "録音中", InterimLines = 3, HintRowX = 845, HintRowY = 986, Screenshot = "shots/b.png" },
                new() { Name = "ずれた", State = "録音中", InterimLines = 3, HintRowX = 845, HintRowY = 948, Screenshot = "shots/c.png" },
            ],
        };
        Assert.Empty(Evaluator.Check(new Expectation { Shots = [new() { Name = "3行", MinInterimLines = 3, SameHintRowAs = "1行" }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "ずれた", SameHintRowAs = "1行" }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "1行", MinInterimLines = 3 }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "3行", MaxInterimLines = 2 }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "3行", SameHintRowAs = "無い" }] }, r));
    }
}
