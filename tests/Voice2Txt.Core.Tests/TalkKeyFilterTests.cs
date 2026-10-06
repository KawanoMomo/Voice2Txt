using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class TalkKeyFilterTests
{
    private const int RCtrl = 0xA3, RAlt = 0xA5, C = 0x43, A = 0x41;

    [Fact]
    public void トークキーは握りつぶし_押下と離しを伝える_リピートは伝えない()
    {
        var f = new TalkKeyFilter(RCtrl);
        var down = f.OnKey(RCtrl, true);
        Assert.True(down.Swallow);
        Assert.Equal(TalkKeySignal.TalkDown, down.Signal);
        var rep = f.OnKey(RCtrl, true);
        Assert.True(rep.Swallow);
        Assert.Equal(TalkKeySignal.None, rep.Signal);
        var up = f.OnKey(RCtrl, false);
        Assert.True(up.Swallow);
        Assert.Equal(TalkKeySignal.TalkUp, up.Signal);
    }

    [Fact]
    public void トークキーを変えると_元のキーは素通しで録音されない()
    {
        var f = new TalkKeyFilter(RAlt);
        Assert.Equal(TalkKeySignal.None, f.OnKey(RCtrl, true).Signal);
        Assert.False(f.OnKey(RCtrl, true).Swallow);
        Assert.False(f.OnKey(RCtrl, false).Swallow);
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(RAlt, true).Signal);
        Assert.Equal(TalkKeySignal.TalkUp, f.OnKey(RAlt, false).Signal);
    }

    [Fact]
    public void 押下中の別キーで取り消し_トークキーと同じキーを合成して送り_以後は素通し()
    {
        var f = new TalkKeyFilter(RCtrl);
        f.OnKey(RCtrl, true);
        var c = f.OnKey(C, true);
        Assert.True(c.Swallow);
        Assert.Equal(TalkKeySignal.OtherKeyWhileTalk, c.Signal);
        Assert.Equal([RCtrl, C], c.InjectDown);
        Assert.False(f.OnKey(C, false).Swallow);           // C の離しは素通し
        Assert.False(f.OnKey(RCtrl, true).Swallow);        // リピートも素通し(修飾キーとして押されている)
        Assert.False(f.OnKey(A, true).Swallow);            // 2 つ目の別キーも素通し(Ctrl+A)
        var up = f.OnKey(RCtrl, false);
        Assert.False(up.Swallow);                          // 離しを素通しして修飾を解く
        Assert.Equal(TalkKeySignal.None, up.Signal);
        Assert.Equal(TalkKeySignal.TalkDown, f.OnKey(RCtrl, true).Signal); // 次の押下はまた録音
    }

    [Fact]
    public void トークキーを押していない間の他キーは素通し()
    {
        var f = new TalkKeyFilter(RCtrl);
        var c = f.OnKey(C, true);
        Assert.False(c.Swallow);
        Assert.Equal(TalkKeySignal.None, c.Signal);
        Assert.Empty(c.InjectDown);
        Assert.False(f.OnKey(C, false).Swallow);
    }

    [Fact]
    public void 期待のキー列が違えば落ちる()
    {
        var r = new VerifyResult { Completed = true, PassedKeys = ["RControlKey down", "RControlKey up"], SentKeys = ["RControlKey down", "C down"] };
        Assert.Empty(Evaluator.Check(new Expectation { PassedKeys = ["RControlKey down", "RControlKey up"], SentKeys = ["RControlKey down", "C down"] }, r));
        Assert.Single(Evaluator.Check(new Expectation { PassedKeys = [] }, r));
        Assert.Single(Evaluator.Check(new Expectation { SentKeys = ["C down"] }, r));
        var warned = new VerifyResult { Completed = true, Warnings = ["talkKey を読めない"] };
        Assert.Single(Evaluator.Check(new Expectation { Warnings = [] }, warned));
        Assert.Empty(Evaluator.Check(new Expectation { Warnings = ["talkKey を読めない"] }, warned));
        var shown = new VerifyResult { Completed = true, States = [new StateRecord { State = "録音中", Text = "録音中  右Alt を離すと入力" }] };
        Assert.Empty(Evaluator.Check(new Expectation { StateTexts = ["右Alt を離すと入力"] }, shown));
        Assert.Single(Evaluator.Check(new Expectation { StateTexts = ["右Ctrl を離すと入力"] }, shown));
    }
}
