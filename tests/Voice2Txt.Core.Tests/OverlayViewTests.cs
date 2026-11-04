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

    public static TheoryData<OverlayState, int, int, bool> CarriedCases => new()
    {
        { OverlayState.Recording, 1, 0, true },
        { OverlayState.ProcessingAndRecording, 2, 1, false },
        { OverlayState.Processing, 0, 1, false },
        { OverlayState.Preparing, 1, 0, false },
        { OverlayState.Evacuated, 1, 0, false },
        { OverlayState.Evacuated, 2, 0, true },
        { OverlayState.Evacuated, 3, 0, false },
        { OverlayState.Evacuated, 1, 2, true },
        { OverlayState.DeliveryFailed, 0, 2, false },
    };

    [Theory]
    [MemberData(nameof(CarriedCases))]
    public void 持ち越した知らせがあっても_部品の文字をつなぐと本文と同じ(OverlayState s, int evac, int failed, bool onClip)
    {
        var v = new OverlayView(s, 1) { Carried = new(evac, failed, onClip) };
        static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));
        Assert.Equal(Squash(v.Text("右Alt")), Squash(string.Concat(v.Parts("右Alt").Select(p => p.Text))));
    }

    [Fact]
    public void 録音中に持ち越した知らせがあれば_案内の後ろに注意の色で件数()
    {
        var v = new OverlayView(OverlayState.Recording) { Carried = new(1, 1, true) };
        Assert.Equal("Text Meter Key Text Warn", Kinds(v));
        Assert.Equal("! 入力できなかった 2件", v.Parts("右Ctrl")[^1].Text);
        Assert.Equal("録音中", v.Label);   // 状態は録音中のまま
    }

    [Fact]
    public void まとめた退避の知らせは_CtrlVとWinVを枠付きにし_上書きした後はCtrlVで貼れますと言わない()
    {
        var over = new OverlayView(OverlayState.Evacuated) { Carried = new(1, 0, false) };
        Assert.Contains(over.Parts("右Ctrl"), p => p is { Kind: OverlayPartKind.Key, Text: "Win+V" });
        Assert.DoesNotContain(over.Parts("右Ctrl"), p => p.Text == "Ctrl+V");
        var two = new OverlayView(OverlayState.Evacuated) { Carried = new(2, 0, true) };
        Assert.Equal(["Ctrl+V", "Win+V"], two.Parts("右Ctrl").Where(p => p.Kind == OverlayPartKind.Key).Select(p => p.Text));
        Assert.Equal("退避", two.Label);
        Assert.Equal("Text Key Text", Kinds(new OverlayView(OverlayState.Evacuated) { Carried = new(1, 0, true) })); // 1 件で貼れるなら MOC のまま
        Assert.Equal(new OverlayView(OverlayState.Evacuated).Text("右Ctrl"), new OverlayView(OverlayState.Evacuated) { Carried = new(1, 0, true) }.Text("右Ctrl"));
    }

    [Fact]
    public void 用意に失敗した表示は詳細をそのまま出し_状態の名前は使えない()
    {
        var msg = Engines.FailedOverlayText("haas");
        var v = new OverlayView(OverlayState.ModelPreparing, 0, msg) { Unavailable = true };
        Assert.Equal(msg, v.Text("右Ctrl"));
        Assert.Equal(msg, Assert.Single(v.Parts("右Ctrl")).Text);
        Assert.Equal(OverlayView.UnavailableLabel, v.Label);
        Assert.Equal("使えない", v.Label);
        Assert.DoesNotContain("準備中", v.Label);
        Assert.Null(v.TransientMs); // 押している間は出し続ける
        Assert.NotEqual(new OverlayView(OverlayState.ModelPreparing, 0, msg), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ダウンロード 42%")]
    public void 本当に準備中なら状態の名前はモデル準備中のまま(string? detail)
    {
        Assert.Equal("モデル準備中", new OverlayView(OverlayState.ModelPreparing, 0, detail).Label);
    }

    [Theory]
    [InlineData(OverlayState.Hidden)]
    [InlineData(OverlayState.Recording)]
    [InlineData(OverlayState.DeliveryFailed)]
    public void 使えないの名前はモデル準備中の表示にだけ付く(OverlayState s)
    {
        Assert.Equal(OverlayView.LabelOf(s), new OverlayView(s) { Unavailable = true }.Label);
    }
}
