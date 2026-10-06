using Voice2Txt.Core;
using Xunit;

namespace Voice2Txt.Core.Tests;

/// <summary>オーバーレイの本文の並び・色の種類が MOC(docs/moc/2026-10-06-ptt-overlay.html)と揃っていること。</summary>
public class OverlayViewTests
{
    private static string Kinds(OverlayView v) => string.Join(" ", v.Parts("右Ctrl").Select(p => p.Kind));

    [Fact]
    public void 録音中は_音量バーが録音中の直後でキー名は枠付き()
    {
        var v = new OverlayView(OverlayState.Recording);
        Assert.Equal("Text Meter Key Text", Kinds(v));
        Assert.Equal("右Ctrl", v.Parts("右Ctrl")[2].Text);
    }

    [Fact]
    public void 処理中と次の録音は_音量バーの後に処理待ちのバッジ()
    {
        var v = new OverlayView(OverlayState.ProcessingAndRecording, 2);
        Assert.Equal("Text Meter Badge", Kinds(v));
        Assert.Equal("処理待ち 2件", v.Parts("右Ctrl")[2].Text);
    }

    [Fact]
    public void 準備中は_赤い点の案内が灰色の補足()
    {
        var p = new OverlayView(OverlayState.Preparing).Parts("右Ctrl");
        Assert.Equal(OverlayPartKind.Note, p[1].Kind);
        Assert.Equal("赤い点が出てから話してください", p[1].Text);
    }

    [Fact]
    public void 退避は_本文が白でCtrlVが枠付き()
    {
        var p = new OverlayView(OverlayState.Evacuated).Parts("右Ctrl");
        Assert.Equal("Text Key Text", Kinds(new OverlayView(OverlayState.Evacuated)));
        Assert.Equal("Ctrl+V", p[1].Text);
        Assert.DoesNotContain(p, x => x.Kind == OverlayPartKind.Note);
    }

    [Theory]
    [InlineData(OverlayState.Preparing)]
    [InlineData(OverlayState.Recording)]
    [InlineData(OverlayState.Processing)]
    [InlineData(OverlayState.ProcessingAndRecording)]
    [InlineData(OverlayState.Pasted)]
    [InlineData(OverlayState.Evacuated)]
    [InlineData(OverlayState.CancelledSilence)]
    [InlineData(OverlayState.Cancelled)]
    [InlineData(OverlayState.ModelPreparing)]
    [InlineData(OverlayState.DeliveryFailed)]
    public void 部品の文字をつなぐと本文と同じ(OverlayState s)
    {
        var v = new OverlayView(s, 1, s == OverlayState.ModelPreparing ? "ダウンロード 42%" : null);
        static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));
        Assert.Equal(Squash(v.Text("右Alt")), Squash(string.Concat(v.Parts("右Alt").Select(p => p.Text))));
    }
}
