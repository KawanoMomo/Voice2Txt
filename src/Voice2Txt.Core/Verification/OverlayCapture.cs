namespace Voice2Txt.Core.Verification;

/// <summary>
/// 検証モードのスクリーンショットが、その実行自身のオーバーレイを写しているかの判定。
/// 画面から写した画素と、同じ表示を自前で描いた画素を、オーバーレイの角丸の内側だけで比べる。
/// 自分のオーバーレイは半透明(不透明度 0.92)なので下の窓が少し透けるが、上に別の窓(並行して走る別の検証モードのオーバーレイ等)が
/// 重なると、文字や枠の位置で大きく食い違う。その割合が閾値を超えたら「自分の表示ではない」とする。
/// </summary>
public static class OverlayCapture
{
    /// <summary>1 画素の R/G/B のどれかがこれより離れていたら食い違いとする(透けの 8% や ClearType の色のにじみより大きい)。</summary>
    public const int PixelTolerance = 96;

    /// <summary>角丸の内側の画素のうち、食い違いがこの割合を超えたら別の表示が写っているとする。</summary>
    public const double MaxMismatchRatio = 0.02;

    /// <summary>角丸の縁は 1 画素ずれやすいので、この幅だけ内側で比べる。</summary>
    public const int EdgeInset = 2;

    /// <summary>角丸(半径 <paramref name="radius"/>)の内側の画素のうち、画面(<paramref name="screen"/>)と自前の描画(<paramref name="render"/>)が食い違う割合。画素は ARGB の int を行優先で並べたもの。</summary>
    public static double MismatchRatio(int[] screen, int[] render, int width, int height, int radius)
    {
        if (screen.Length < width * height || render.Length < width * height) throw new ArgumentException("画素の数が幅×高さに足りない");
        int compared = 0, differ = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!InsideRounded(width, height, radius, EdgeInset, x, y)) continue;
                compared++;
                if (Differs(screen[y * width + x], render[y * width + x])) differ++;
            }
        return compared == 0 ? 0 : (double)differ / compared;
    }

    /// <summary>画面の写しが自分の表示だと言えるか(食い違いが閾値以下)。</summary>
    public static bool LooksLikeOwn(double mismatchRatio) => mismatchRatio <= MaxMismatchRatio;

    public static bool Differs(int a, int b)
    {
        int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
        int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
        int db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return Math.Max(dr, Math.Max(dg, db)) > PixelTolerance;
    }

    /// <summary>幅×高さの角丸四角(半径 radius)を inset だけ縮めた内側に (x, y) の画素があるか。</summary>
    public static bool InsideRounded(int width, int height, int radius, int inset, int x, int y)
    {
        double l = inset, t = inset, r = width - 1 - inset, b = height - 1 - inset;
        if (x < l || x > r || y < t || y > b) return false;
        double rad = Math.Max(0, Math.Min(radius, Math.Min(r - l, b - t) / 2.0) - inset);
        double cx = x < l + rad ? l + rad : x > r - rad ? r - rad : x;
        double cy = y < t + rad ? t + rad : y > b - rad ? b - rad : y;
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= rad * rad;
    }
}
