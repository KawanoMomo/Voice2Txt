namespace Voice2Txt.Core;

/// <summary>
/// オーバーレイの配置(px)。常に出る案内の行(状態の印と本文)は一番下に置き、途中経過はその上に積む。
/// 窓は画面下端からの距離が一定なので、途中経過の行数・幅が変わっても案内の行は画面上で動かない
/// (縦: 窓の下端から行の高さぶん上。横: 窓の中央に置き、窓も画面の中央に置く)。
/// </summary>
/// <param name="Width">窓の幅。</param>
/// <param name="Height">窓の高さ。</param>
/// <param name="RowLeft">案内の行の左端(窓の中の x)。</param>
/// <param name="RowTop">案内の行の上端(窓の中の y)。途中経過があれば区切り線をここに引く。</param>
/// <param name="InterimTop">途中経過の欄の上端(窓の中の y。途中経過が無ければ 0)。</param>
public readonly record struct OverlayLayout(int Width, int Height, int RowLeft, int RowTop, int InterimTop)
{
    /// <summary>左右の余白(MOC の padding)。</summary>
    public const int PadX = 16;

    /// <summary>途中経過の欄の上の余白と、欄と区切り線の間。</summary>
    public const int InterimPadTop = 10, InterimGap = 4;

    /// <summary>画面の作業領域の下端から窓の下端までの距離。</summary>
    public const int BottomMargin = 18;

    /// <param name="rowW">案内の行の中身の幅(状態の印から本文の終わりまで)。</param>
    /// <param name="rowH">案内の行の高さ。</param>
    /// <param name="interimW">途中経過の欄の幅(無ければ 0)。</param>
    /// <param name="interimH">途中経過の欄の高さ(無ければ 0)。</param>
    public static OverlayLayout Of(int rowW, int rowH, int interimW, int interimH)
    {
        bool interim = interimH > 0;
        int width = Math.Max(rowW + 2 * PadX, interim ? interimW + 2 * PadX : 0);
        int rowLeft = PadX + (width - rowW - 2 * PadX) / 2;
        if (!interim) return new(width, rowH, rowLeft, 0, 0);
        int rowTop = InterimPadTop + interimH + InterimGap;
        return new(width, rowTop + rowH, rowLeft, rowTop, InterimPadTop);
    }

    /// <summary>
    /// 作業領域(左端・幅・下端)の中での窓の左上。案内の行の画面上の左端が、窓の幅によらず
    /// 「途中経過の無い窓を中央に置いたとき」と同じ所になるように置く(端数の 1px も揃える)。
    /// </summary>
    public (int Left, int Top) Place(int areaLeft, int areaWidth, int areaBottom, int rowW)
    {
        int rowScreenLeft = areaLeft + (areaWidth - rowW - 2 * PadX) / 2 + PadX;
        return (rowScreenLeft - RowLeft, areaBottom - BottomMargin - Height);
    }
}
