namespace Voice2Txt.Core.Verification;

/// <summary>モデルの用意の状態(検証モードの台本から見た)。</summary>
public enum ModelReadiness { Preparing, Ready, Failed }

/// <summary>
/// 台本の手 holdUntilAudioEnd の判定。押下が録音になっていない(モデル準備中・用意の失敗・トークキーでない)なら、
/// 流し終わりは来ないので待たずに返す。そのときの理由(verify.log に残す)を返す。録音中なら null(流し終わるまで待つ)。
/// </summary>
public static class HoldUntilAudioEnd
{
    public static string? SkipReason(bool recording, ModelReadiness model) => recording ? null : model switch
    {
        ModelReadiness.Preparing => "モデル準備中のため録音していない",
        ModelReadiness.Failed => "エンジンを用意できなかったため録音していない",
        _ => "押下が録音になっていない(トークキーでない・押していない)",
    };
}
