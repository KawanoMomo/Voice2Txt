namespace Voice2Txt.Core;

/// <summary>
/// 確定版の届け方(設定 <see cref="AppSettings.Delivery"/>)。
/// evacuate(初期値): 離した後、貼る時点の前面が押した時点の貼り付け先ウィンドウなら Ctrl+V、違えば貼らずにクリップボードへ退避する。
/// return: 前面が変わっていても押した時点のウィンドウを前面に戻して Ctrl+V で貼り、元の前面へ戻す。貼った後はクリップボードを貼る前の中身に戻す
/// (前面に戻せなければ evacuate と同じく退避し、クリップボードには確定版を残す)。
/// </summary>
public static class Deliveries
{
    public const string Evacuate = "evacuate";
    public const string Return = "return";

    /// <summary>設定に書ける値と画面に出す名前。</summary>
    public static readonly IReadOnlyList<SettingChoice> Choices =
    [
        new(Evacuate, "前面が変わっていたら退避(既定)"),
        new(Return, "押した時のウィンドウへ戻して貼る"),
    ];

    /// <summary>設定の値を読む(大文字小文字・前後の空白は問わない。画面の名前も可)。空は初期値。読めなければ null。</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Evacuate;
        return Choices.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Label, v, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>return か(読めない値は初期値 evacuate と同じ)。</summary>
    public static bool IsReturn(string? value) => Normalize(value) == Return;
}
