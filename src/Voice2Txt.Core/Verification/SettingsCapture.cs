namespace Voice2Txt.Core.Verification;

/// <summary>
/// 検証モードが撮る設定画面のスクショで、選択式の項目の値が写っているかの判定。
/// 選択式の部品(ドロップダウン)の文字の欄(枠と▼を除いた内側)に、文字の色(暗い画素)が 1 つも無ければ空欄に写っているとする。
/// 撮り方(部品に自分を描かせる render)によっては、画面には値が出ていても欄が空のまま写ることがあるため、撮った画を確かめてから残す。
/// </summary>
public static class SettingsCapture
{
    /// <summary>文字とみなす暗さ(R/G/B の平均がこれ未満)。欄の地(白・薄い灰)と枠の灰色より暗い。</summary>
    public const int InkLevel = 128;

    /// <summary>
    /// 行優先の ARGB 画素(幅 <paramref name="width"/>)のうち、矩形(<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/>)に
    /// 文字の色の画素があるか。矩形が画の外にはみ出した分は見ない。
    /// </summary>
    public static bool HasInk(int[] pixels, int width, int x, int y, int w, int h)
    {
        if (width <= 0) return false;
        int height = pixels.Length / width;
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(width, x + w), y1 = Math.Min(height, y + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int p = pixels[yy * width + xx];
                int avg = (((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF)) / 3;
                if (avg < InkLevel) return true;
            }
        return false;
    }

    /// <summary>
    /// 撮った画で値が写っていない選択式の項目のキー(値が空の項目は数えない)。<paramref name="fields"/> は項目ごとのキー・画面に出す値・文字の欄(画の座標)。
    /// </summary>
    public static List<string> BlankFields(int[] pixels, int width, IEnumerable<(string Key, string Text, int X, int Y, int W, int H)> fields) =>
        fields.Where(f => f.Text.Length > 0 && f.W > 0 && f.H > 0 && !HasInk(pixels, width, f.X, f.Y, f.W, f.H)).Select(f => f.Key).ToList();

    /// <summary>結果の capture の文字列: 写し方と、撮った画で空欄だった項目・画面から写した項目(<paramref name="note"/>)・自前で書き足した項目。</summary>
    public static string Describe(string method, IReadOnlyCollection<string> renderBlank, IReadOnlyCollection<string> filled, string? note)
    {
        var notes = new List<string>();
        if (renderBlank.Count > 0) notes.Add("render left blank: " + string.Join(",", renderBlank));
        if (note is not null) notes.Add(note);
        if (filled.Count > 0) notes.Add("filled by verify: " + string.Join(",", filled));
        return notes.Count == 0 ? method : $"{method} ({string.Join("; ", notes)})";
    }
}
