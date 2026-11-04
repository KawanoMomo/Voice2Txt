namespace Voice2Txt.Core;

/// <summary>オーバーレイの状態(MOC の 8 つ + 押下が短い・他キーの取り消し + 入力失敗 + 非表示)。</summary>
public enum OverlayState
{
    Hidden,
    Preparing,          // 準備中
    Recording,          // 録音中
    Processing,         // 処理中
    ProcessingAndRecording, // 処理中+次を録音中
    Pasted,             // 貼り付け完了
    Evacuated,          // 退避
    CancelledSilence,   // 取り消し(無音)
    Cancelled,          // 取り消し(押下が短い)
    ModelPreparing,     // モデル準備中
    DeliveryFailed,     // 入力失敗(確定版をクリップボードに入れられなかった。退避と違い Ctrl+V では貼れない)
}

/// <summary>
/// 持ち越した知らせ(CONTEXT.md): 次の発話の録音中・処理中に起きた退避と入力失敗の件数。押下中・処理中の行には件数だけを出し、
/// 録音と処理が済んだら退避・入力失敗の知らせとしてまとめて出す。
/// </summary>
/// <param name="Evacuated">退避した確定版の数。</param>
/// <param name="Failed">入力失敗の数(確定版はクリップボードにも無い)。</param>
/// <param name="LastEvacuatedOnClipboard">最後に退避した確定版が、まだクリップボードに入っているか(後の貼り付けで上書きしたら false。Ctrl+V では出ず Win+V の履歴にある)。</param>
public sealed record CarriedNotice(int Evacuated, int Failed, bool LastEvacuatedOnClipboard)
{
    public int Count => Evacuated + Failed;

    /// <summary>今までの 1 件だけの知らせ(退避 1 件で Ctrl+V で貼れる、または入力失敗 1 件)と同じ文言で済むか。</summary>
    public bool IsSingle => (Evacuated == 1 && Failed == 0 && LastEvacuatedOnClipboard) || (Evacuated == 0 && Failed == 1);
}

/// <param name="Interim">途中経過(押下中にそれまでの音声から作り直した暫定の文字起こし)。オーバーレイの本文の下に出すだけで、届けない。</param>
public sealed record OverlayView(OverlayState State, int PendingCount = 0, string? Detail = null, string? Interim = null)
{
    public static readonly OverlayView Hidden = new(OverlayState.Hidden);

    /// <summary>
    /// 持ち越した知らせ。押下中・処理中の状態では行の末尾に「! 入力できなかった n件」を足し、退避・入力失敗の状態では件数とクリップボードに
    /// 残っているかに合わせた文言にする。null なら今までどおり。
    /// </summary>
    public CarriedNotice? Carried { get; init; }

    private bool Busy => State is OverlayState.Preparing or OverlayState.Recording or OverlayState.Processing or OverlayState.ProcessingAndRecording;

    /// <summary>押下中・処理中の行の末尾に足す、持ち越した知らせの件数(無ければ null)。</summary>
    private string? CarriedBadge => Busy && Carried is { Count: > 0 } c ? $"! 入力できなかった {c.Count}件" : null;

    /// <summary>
    /// モデル準備中の表示のうち、用意に失敗して終わっている(再起動まで使えない)もの。本文は <see cref="Detail"/>(使えないことと直し方)をそのまま出し、
    /// 待たせる語(「準備中」「完了まで」)を出さない。状態の名前(<see cref="Label"/>)も「使えない」にする(ログ・検証モードの結果・スクショ名)。
    /// </summary>
    public bool Unavailable { get; init; }

    /// <summary>用意に失敗して再起動まで音声入力ができない状態の名前(CONTEXT.md「使えない」)。</summary>
    public const string UnavailableLabel = "使えない";

    /// <summary>途中経過の欄に収める最大の行数。超えたら頭を削り、末尾(いま話している所)を残す。</summary>
    public const int InterimMaxLines = 3;

    /// <summary>途中経過を欄に収める: 収まらなければ頭から削って「…」を付ける。<paramref name="fits"/> は文字列が欄に収まるか。</summary>
    public static string FitInterim(string text, Func<string, bool> fits)
    {
        if (fits(text)) return text;
        int lo = 1, hi = text.Length; // 頭から削る文字数(二分探索で最小を探す)
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (fits("…" + text[mid..])) hi = mid; else lo = mid + 1;
        }
        return "…" + text[lo..];
    }

    /// <summary>状態の名前(結果 JSON・スクショのファイル名・Verify の期待に使う)。</summary>
    public string Label => State == OverlayState.ModelPreparing && Unavailable ? UnavailableLabel : LabelOf(State);

    public static string LabelOf(OverlayState s) => s switch
    {
        OverlayState.Hidden => "非表示",
        OverlayState.Preparing => "準備中",
        OverlayState.Recording => "録音中",
        OverlayState.Processing => "処理中",
        OverlayState.ProcessingAndRecording => "処理中+次を録音中",
        OverlayState.Pasted => "貼り付け完了",
        OverlayState.Evacuated => "退避",
        OverlayState.CancelledSilence => "取り消し(無音)",
        OverlayState.Cancelled => "取り消し",
        OverlayState.ModelPreparing => "モデル準備中",
        OverlayState.DeliveryFailed => "入力失敗",
        _ => s.ToString(),
    };

    /// <summary>一瞬だけ出して消える状態。消えるまでのミリ秒(MOC の例)。</summary>
    public int? TransientMs => State switch
    {
        OverlayState.Pasted => 800,
        OverlayState.Evacuated => Carried is null || Carried.IsSingle ? 4000 : 6000, // まとめた知らせは長いので入力失敗と同じだけ出す
        OverlayState.CancelledSilence => 1500,
        OverlayState.Cancelled => 1200,
        OverlayState.DeliveryFailed => 6000,
        _ => null,
    };

    /// <summary>
    /// モデル準備中・使えないときに押して離した後も、その知らせを出しておくミリ秒(押している間は出し続ける)。
    /// 使えない理由と直し方は長いので、入力失敗の知らせと同じだけ出す。準備中の進み具合は退避の知らせと同じだけ。
    /// </summary>
    public static int ModelNoticeMs(bool unavailable) => unavailable ? 6000 : 4000;

    /// <summary>オーバーレイに出す本文(MOC の文言)。</summary>
    /// <param name="talkKeyName">録音を止めるキーの名前(トークキー。押さない録音では終了キーの組み合わせ)。</param>
    /// <param name="toggle">押さない録音(録音中は「{終了キー} で入力」。押している間だけ録音なら「{トークキー} を離すと入力」)。</param>
    public string Text(string talkKeyName, bool toggle = false)
    {
        string text = State switch
        {
            OverlayState.Preparing => "準備中…  赤い点が出てから話してください",
            OverlayState.Recording => $"録音中  {talkKeyName} {StopVerb(toggle)}",
            OverlayState.Processing => "文字起こし中…",
            OverlayState.ProcessingAndRecording => $"録音中  処理待ち {PendingCount}件",
            OverlayState.Pasted => "入力しました",
            OverlayState.Evacuated => EvacuatedText(Carried),
            OverlayState.CancelledSilence => "音声が検出されませんでした",
            OverlayState.Cancelled => $"取り消しました({Detail ?? "押す時間が短すぎます"})",
            OverlayState.ModelPreparing when Unavailable => Detail ?? "モデルを用意できないため音声入力は使えません",
            OverlayState.ModelPreparing => $"モデル準備中{(Detail is null ? "" : $"({Detail})")} — 完了まで音声入力は使えません",
            OverlayState.DeliveryFailed => FailedText(Carried?.Failed ?? 1),
            _ => "",
        };
        return CarriedBadge is { } badge ? $"{text}  {badge}" : text;
    }

    /// <summary>
    /// 退避の知らせ。1 件で Ctrl+V で貼れるなら MOC の文言。後の貼り付けで上書きした・何件もまとめたときは、Ctrl+V で出るのが
    /// どれか(どれも出ないか)と Win+V の履歴にあることを言う。入力失敗も混じっていれば件数を足す。
    /// </summary>
    private static string EvacuatedText(CarriedNotice? c)
    {
        int n = c?.Evacuated ?? 1;
        bool on = c?.LastEvacuatedOnClipboard ?? true;
        string head = n <= 1 ? "ウィンドウが変わったため貼り付けませんでした" : $"ウィンドウが変わったため {n}件 貼り付けませんでした";
        string tail = !on ? "後の入力で上書きしたため Win+V の履歴から貼れます"
            : n <= 1 ? "Ctrl+V で貼れます"
            : "最後の 1件は Ctrl+V、前の分は Win+V の履歴から貼れます";
        string text = $"{head} — {tail}";
        if (c is { Failed: > 0 } f) text += $"(ほかに {f.Failed}件 はクリップボードを使用中のため入力できませんでした — もう一度話してください)";
        return text;
    }

    private static string FailedText(int n) => n <= 1
        ? "他のアプリがクリップボードを使用中のため入力できませんでした — もう一度話してください"
        : $"他のアプリがクリップボードを使用中のため {n}件 入力できませんでした — もう一度話してください";

    /// <summary>文言をキー名(Ctrl+V・Win+V)の所で分け、キー名は枠付きの部品にする。</summary>
    private static List<OverlayPart> WithKeys(string text)
    {
        var parts = new List<OverlayPart>();
        foreach (var piece in System.Text.RegularExpressions.Regex.Split(text, "(Ctrl\\+V|Win\\+V)"))
        {
            if (piece is "Ctrl+V" or "Win+V") parts.Add(new(OverlayPartKind.Key, piece));
            else if (piece.Trim() is { Length: > 0 } t) parts.Add(new(OverlayPartKind.Text, t));
        }
        return parts;
    }

    /// <summary>
    /// 本文を MOC の並び・色で描くための部品列(状態の印 ●・✓・! の後ろに左から並べる)。
    /// 部品の文字をつなぐと(空白を除いて)<see cref="Text"/> と同じになる。音量バーは文字を持たない。
    /// </summary>
    public IReadOnlyList<OverlayPart> Parts(string talkKeyName, bool toggle = false)
    {
        List<OverlayPart> parts = State switch
        {
            OverlayState.Preparing => [new(OverlayPartKind.Text, "準備中…"), new(OverlayPartKind.Note, "赤い点が出てから話してください")],
            OverlayState.Recording => [new(OverlayPartKind.Text, "録音中"), OverlayPart.Meter, new(OverlayPartKind.Key, talkKeyName), new(OverlayPartKind.Text, StopVerb(toggle))],
            OverlayState.Processing => [new(OverlayPartKind.Text, "文字起こし中…")],
            OverlayState.ProcessingAndRecording => [new(OverlayPartKind.Text, "録音中"), OverlayPart.Meter, new(OverlayPartKind.Badge, $"処理待ち {PendingCount}件")],
            OverlayState.Evacuated => WithKeys(EvacuatedText(Carried)),
            OverlayState.Hidden => [],
            _ => [new(OverlayPartKind.Text, Text(talkKeyName, toggle))],
        };
        // 持ち越した知らせの件数は、押下中・処理中の行の末尾に注意の色で出す(録音中の印と案内はそのまま)
        if (CarriedBadge is { } badge) parts.Add(new(OverlayPartKind.Warn, badge));
        return parts;
    }

    private static string StopVerb(bool toggle) => toggle ? "で入力" : "を離すと入力";
}

/// <summary>オーバーレイの本文の部品の種類(MOC の CSS に対応)。</summary>
public enum OverlayPartKind
{
    Text,   // 白の地の文
    Note,   // 灰色(#bbb)の補足(MOC の .note)
    Key,    // 枠付きのキー名(MOC の .kbd)
    Badge,  // 丸いバッジ(MOC の .badge)
    Meter,  // 音量バー(MOC の .meter)
    Warn,   // 注意の色(MOC の .warn #ffd166)の地の文。押下中・処理中の行に足す持ち越した知らせの件数
}

public sealed record OverlayPart(OverlayPartKind Kind, string Text)
{
    public static readonly OverlayPart Meter = new(OverlayPartKind.Meter, "");
}
