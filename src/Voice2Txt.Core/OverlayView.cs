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

public sealed record OverlayView(OverlayState State, int PendingCount = 0, string? Detail = null)
{
    public static readonly OverlayView Hidden = new(OverlayState.Hidden);

    /// <summary>状態の名前(結果 JSON・スクショのファイル名・Verify の期待に使う)。</summary>
    public string Label => LabelOf(State);

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
        OverlayState.Evacuated => 4000,
        OverlayState.CancelledSilence => 1500,
        OverlayState.Cancelled => 1200,
        OverlayState.DeliveryFailed => 6000,
        _ => null,
    };

    /// <summary>オーバーレイに出す本文(MOC の文言)。</summary>
    public string Text(string talkKeyName) => State switch
    {
        OverlayState.Preparing => "準備中…  赤い点が出てから話してください",
        OverlayState.Recording => $"録音中  {talkKeyName} を離すと入力",
        OverlayState.Processing => "文字起こし中…",
        OverlayState.ProcessingAndRecording => $"録音中  処理待ち {PendingCount}件",
        OverlayState.Pasted => "入力しました",
        OverlayState.Evacuated => "ウィンドウが変わったため貼り付けませんでした — Ctrl+V で貼れます",
        OverlayState.CancelledSilence => "音声が検出されませんでした",
        OverlayState.Cancelled => $"取り消しました({Detail ?? "押す時間が短すぎます"})",
        OverlayState.ModelPreparing => $"モデル準備中{(Detail is null ? "" : $"({Detail})")} — 完了まで音声入力は使えません",
        OverlayState.DeliveryFailed => "他のアプリがクリップボードを使用中のため入力できませんでした — もう一度話してください",
        _ => "",
    };
}
