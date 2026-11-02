using System.Text;

namespace Voice2Txt.Core;

/// <summary>
/// 日本語の文字と文字の間に whisper が書いた空白を詰める後処理(「…取り組みます。 以上です。」→「…取り組みます。以上です。」)。
/// 届ける直前に 1 回だけ掛ける(言い淀みを取り除いた後。途中経過にも同じく掛ける)。設定では止めない(日本語の文の間に空白は入らない)。
/// 詰めるのは空白の前後がどちらも日本語の文字(かな・漢字・全角の句読点と記号)のときと、日本語の句読点と数字の間だけ。
/// 英文の語と語の間、英文と日本語の境(「booking. と」「、 Please」)の空白はそのまま残す。語は置き換えない(空白だけを消す)。
/// </summary>
public static class Spacing
{
    /// <summary>日本語の文字の間の空白を詰めた文字列。詰める所が無ければ同じ文字列を返す。</summary>
    public static string Tidy(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        StringBuilder? sb = null;
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsWhiteSpace(text[i]) || i == 0) { sb?.Append(text[i]); i++; continue; }
            int j = i;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j < text.Length && Joins(text[i - 1], text[j]))
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                i = j;
                continue;
            }
            if (sb is not null) sb.Append(text, i, j - i);
            i = j;
        }
        return sb?.ToString() ?? text;
    }

    /// <summary>a と b の間の空白を詰めるか。</summary>
    private static bool Joins(char a, char b) =>
        IsJapanese(a) && IsJapanese(b)
        || IsJapanesePunctuation(a) && char.IsAsciiDigit(b)
        || char.IsAsciiDigit(a) && IsJapanesePunctuation(b);

    /// <summary>かな・漢字・全角の句読点と記号(和文の約物・全角英数・半角カナを含む)。</summary>
    private static bool IsJapanese(char c) => !char.IsWhiteSpace(c) && (c is '…' or '‥'
        || c is >= '⺀' and <= '鿿'   // 部首・約物(、。「」)・かな・漢字
        || c is >= '豈' and <= '﫿'   // 互換漢字
        || c is >= '！' and <= 'ﾟ'); // 全角英数・記号、半角カナ・句読点

    private static bool IsJapanesePunctuation(char c) => c is '、' or '。' or '，' or '．' or '！' or '？' or '｡' or '､' or '「' or '」' or '『' or '』' or '（' or '）' or '…';
}
