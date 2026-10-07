namespace Voice2Txt.Core;

/// <summary>whisper.cpp に渡す復号の設定(どのモデルでも同じ)。</summary>
public static class Decoding
{
    /// <summary>話す言葉。日本語固定(仕様 5 章 8)。</summary>
    public const string Language = "ja";

    /// <summary>
    /// 初期プロンプト。言い淀み(えーと・あの・ええと・その・まあ・えっと)をかなで書いた話し言葉を先に見せ、
    /// 言い淀みを英字(「ええと」→「A と」)や別の語に当てずに、かなのまま書かせる。
    /// 続けて数える列の終わり(9つ、10。)を見せ、「…ここのつ、とお。」の「とお」(「つ」が付かない短い数)を「等」などの別の語でなく数として書かせる。
    /// 語の置換表・後処理ではない(届いた文字列は書き換えない)。短く保つ: 長いと無音・短い発話で幻聴の種になる(数える列を長く見せると物音が「1つ、10。」になる)。
    /// </summary>
    public const string InitialPrompt = "えーと、あの、ええと、その、まあ、えっと。9つ、10。";
}
