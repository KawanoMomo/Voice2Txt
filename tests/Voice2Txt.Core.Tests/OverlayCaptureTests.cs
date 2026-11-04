using Voice2Txt.Core.Verification;
using Xunit;

namespace Voice2Txt.Core.Tests;

/// <summary>
/// 検証モードのスクショが、その実行自身のオーバーレイだけを写しているかの判定。
/// 並行して走る別の検証モードのオーバーレイが上に重なった写しは「自分の表示ではない」とし、自前の描画に代える。
/// </summary>
public class OverlayCaptureTests
{
    const int W = 240, H = 36, R = 18;
    static readonly int Bg = Argb(28, 28, 30), White = Argb(255, 255, 255), Desktop = Argb(0, 120, 215);

    static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    /// <summary>オーバーレイ風の画: 地は暗色、x0〜x1 に白い文字の帯(縦 12〜24)。</summary>
    static int[] Overlay(int x0, int x1)
    {
        var px = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = x >= x0 && x < x1 && y >= 12 && y < 24 && (x / 3) % 2 == 0 ? White : Bg;
        return px;
    }

    /// <summary>不透明度 a の自分の表示が、色 under の上に重なって見える画素。</summary>
    static int[] Blend(int[] own, int under, double a) => own.Select(c =>
    {
        int Ch(int v, int s) => (int)Math.Round(a * ((v >> s) & 0xFF) + (1 - a) * ((under >> s) & 0xFF));
        return Argb(Ch(c, 16), Ch(c, 8), Ch(c, 0));
    }).ToArray();

    [Fact]
    public void 自分の表示だけが写っていれば_下の窓が透けても自分の表示とみなす()
    {
        var own = Overlay(30, 200);
        Assert.Equal(0, OverlayCapture.MismatchRatio(own, own, W, H, R));
        var screen = Blend(own, White, 0.92); // 不透明度 0.92、下は白い窓
        double m = OverlayCapture.MismatchRatio(screen, own, W, H, R);
        Assert.True(OverlayCapture.LooksLikeOwn(m), $"mismatch={m}");
    }

    [Fact]
    public void 角丸の外は比べない()
    {
        var own = Overlay(30, 200);
        var screen = (int[])own.Clone();
        screen[0] = screen[W - 1] = screen[(H - 1) * W] = screen[H * W - 1] = Desktop; // 四隅は下の窓が見える
        Assert.Equal(0, OverlayCapture.MismatchRatio(screen, own, W, H, R));
        Assert.False(OverlayCapture.InsideRounded(W, H, R, 0, 0, 0));
        Assert.True(OverlayCapture.InsideRounded(W, H, R, 0, W / 2, H / 2));
        Assert.True(OverlayCapture.InsideRounded(W, H, R, 0, R, 0));
    }

    [Fact]
    public void 別の実行の幅の狭いオーバーレイが上に重なった写しは_自分の表示ではない()
    {
        // 自分は「文字起こし中…」(幅いっぱい)、上に別の実行の「録音中」(左の 140px だけ・文字の位置が違う)。右はその窓の外の机上が見える
        var own = Overlay(30, 200);
        var other = Overlay(60, 120);
        var screen = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                screen[y * W + x] = x < 140 ? other[y * W + x] : Desktop;
        double m = OverlayCapture.MismatchRatio(screen, own, W, H, R);
        Assert.False(OverlayCapture.LooksLikeOwn(m), $"mismatch={m}");
    }

    [Fact]
    public void 同じ大きさの別の表示が上に重なっても_文字の位置が違えば自分の表示ではない()
    {
        var own = Overlay(30, 200);
        var screen = Overlay(100, 230);
        Assert.False(OverlayCapture.LooksLikeOwn(OverlayCapture.MismatchRatio(screen, own, W, H, R)));
    }

    const int TW = 560, TH = 110, RowTop = 74;

    /// <summary>途中経過 3 行の上に案内の行がある高い窓の画: 途中経過は幅いっぱいの文字の帯、案内の行は x0〜x1 の文字の帯(縦 8 画素。phase で文字の位置が違う)。</summary>
    static int[] Tall(int x0, int x1, int phase)
    {
        var px = new int[TW * TH];
        for (int y = 0; y < TH; y++)
            for (int x = 0; x < TW; x++)
            {
                bool interim = x >= 16 && x < TW - 16 && (y is >= 12 and < 24 or >= 34 and < 46 or >= 56 and < 68) && (x / 3) % 2 == 0;
                bool hint = x >= x0 && x < x1 && y >= 88 && y < 96 && (x / 3 + phase) % 2 == 0;
                px[y * TW + x] = interim || hint ? White : Bg;
            }
        return px;
    }

    [Fact]
    public void 途中経過が大半を占め全体の食い違いが閾値未満でも_案内の行が別の文言なら自分の表示ではない()
    {
        // 自分は「文字起こし中…」、上に別の実行の「✓ 入力しました」が案内の行だけに重なる(途中経過は自分のものが見えている)
        var own = Tall(220, 330, 0);
        var screen = Tall(220, 330, 1);
        double whole = OverlayCapture.MismatchRatio(screen, own, TW, TH, R);
        double row = OverlayCapture.MismatchRatio(screen, own, TW, TH, R, RowTop);
        Assert.True(OverlayCapture.LooksLikeOwn(whole), $"whole={whole}");
        Assert.False(OverlayCapture.LooksLikeOwn(row), $"row={row}");
        Assert.StartsWith("overlapped: hint row", OverlayCapture.RejectReason(whole, row, [], "Voice2Txt"));
        // 自分だけが写っていれば(下の窓が透けても)画面の写しを使う
        var alone = Blend(own, White, 0.92);
        Assert.Null(OverlayCapture.RejectReason(OverlayCapture.MismatchRatio(alone, own, TW, TH, R),
            OverlayCapture.MismatchRatio(alone, own, TW, TH, R, RowTop), ["explorer#10"], "Voice2Txt"));
    }

    [Fact]
    public void 上に同じ製品の別の窓が重なっていれば_画素が一致しても画面の写しを使わない()
    {
        Assert.Equal("overlapped: another Voice2Txt window above", OverlayCapture.RejectReason(0, 0, ["Voice2Txt#4242"], "Voice2Txt"));
        Assert.Null(OverlayCapture.RejectReason(0, 0, ["Voice2TxtHelper#1", "explorer#2"], "Voice2Txt"));
        Assert.StartsWith("overlapped: ", OverlayCapture.RejectReason(0.05, 0, [], "Voice2Txt"));
    }

    [Fact]
    public void 画素が足りなければ投げる()
    {
        Assert.Throws<ArgumentException>(() => OverlayCapture.MismatchRatio(new int[3], new int[W * H], W, H, R));
    }
}
