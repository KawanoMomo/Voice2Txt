using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class HoldUntilAudioEndTests
{
    [Theory]
    [InlineData(ModelReadiness.Preparing)]
    [InlineData(ModelReadiness.Ready)]
    [InlineData(ModelReadiness.Failed)]
    public void 録音中なら流し終わるまで待つ(ModelReadiness model)
    {
        Assert.Null(HoldUntilAudioEnd.SkipReason(recording: true, model));
    }

    [Fact]
    public void モデル準備中の押下は待たずに理由付きで返す()
    {
        Assert.Equal("モデル準備中のため録音していない", HoldUntilAudioEnd.SkipReason(recording: false, ModelReadiness.Preparing));
    }

    [Fact]
    public void 用意に失敗した後の押下も待たずに返す()
    {
        Assert.Contains("用意できなかった", HoldUntilAudioEnd.SkipReason(recording: false, ModelReadiness.Failed));
    }

    [Fact]
    public void 録音にならない押下は用意ができていても待たない()
    {
        Assert.NotNull(HoldUntilAudioEnd.SkipReason(recording: false, ModelReadiness.Ready));
    }

    [Fact]
    public void 期待の待たずに返した理由が違えば落ちる()
    {
        var skipped = new VerifyResult { Completed = true, HoldSkips = ["モデル準備中のため録音していない"] };
        Assert.Empty(Evaluator.Check(new Expectation { HoldSkips = ["モデル準備中のため録音していない"] }, skipped));
        Assert.Single(Evaluator.Check(new Expectation { HoldSkips = [] }, skipped));
        Assert.Empty(Evaluator.Check(new Expectation(), skipped)); // 期待に書かなければ見ない
    }
}
