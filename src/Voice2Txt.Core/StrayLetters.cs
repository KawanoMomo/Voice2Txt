using System.Text;

namespace Voice2Txt.Core;

/// <summary>
/// 確定版に残った、発話に無い 1 文字の英字(はぐれ英字)を取り除く後処理。Haas は言い淀み・息を「h」「a」のような小文字 1 文字で書くことがある
/// (「ええと」→「aと」)。engine=haas のときだけ掛ける(<see cref="PttOptions.RemoveStrayLetters"/>)。whisper は断片を書かないので掛けない。
/// whisper も Haas も日本語の中の変数・軸の名前を小文字 1 文字で書く(「x軸とy軸」「変数xに」「n の」)ので、日本語に付いた英字は残す(話した語を欠かさないのが先)。
/// 言い淀みを取り除く前に 1 回だけ掛ける(途中経過にも同じく掛ける)。設定では止めない(発話に無い文字で、言い淀みと違って話した語ではない)。
/// 取り除くのは、小文字 1 文字の英字で、前後(空白を飛ばした隣)が英字でも数字でもなく、すぐ隣が語をつなぐ記号(. - _ / @ ' &amp; + # : ~)でもなく、
/// さらに「ええと」を書いた「aと」の形(<see cref="IsEetoSpelling"/>)か、どの語にも付いていない形(<see cref="IsIsolated"/>)のものだけ。
/// 大文字 1 文字(「A案」「Cさん」「ビタミンC」)、英単語・英文の語(「a few」「I have a pen」)、型番・単位(「3x4」「5 m」「e-mail」)は残す。
/// 取り除いた所の前後の空白は日本語どうしなら詰め、読点が重なれば 1 つにする。
/// </summary>
public static class StrayLetters
{
    /// <summary>はぐれ英字を取り除いた文字列と、取り除いた数。無ければ同じ文字列を返す。</summary>
    public static (string Text, int Removed) Remove(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, 0);
        StringBuilder? sb = null;
        int removed = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!IsStray(text, i))
            {
                sb?.Append(c);
                continue;
            }
            sb ??= new StringBuilder(text, 0, i, text.Length);
            removed++;
            // 前の空白と後ろの空白は 1 つにまとめる(日本語どうしの空白は後の Spacing.Tidy が詰める)
            int j = i + 1;
            if (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            // 「a と」の間の空白は「と」に寄せる(「三部a と五分」→「三部と五分」)
            if (IsEetoSpelling(text, i)) while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            // 読点・句点が重なるなら後ろを捨てる(「三部、a、五部」→「三部、五部」)
            int k = sb.Length - 1;
            while (k >= 0 && char.IsWhiteSpace(sb[k])) k--;
            if (k >= 0 && IsMark(sb[k]))
            {
                int m = j;
                while (m < text.Length && char.IsWhiteSpace(text[m])) m++;
                if (m < text.Length && IsComma(text[m])) { sb.Length = k + 1; j = m + 1; }
            }
            // 文頭のはぐれ英字の後ろの読点・空白は消す(「a、資料は」→「資料は」)
            if (k < 0)
            {
                while (j < text.Length && (char.IsWhiteSpace(text[j]) || IsComma(text[j]))) j++;
                sb.Clear();
            }
            i = j - 1;
        }
        if (sb is null) return (text, 0);
        return (sb.ToString().Trim(), removed);
    }

    /// <summary>
    /// text[i] がはぐれ英字か。小文字 1 文字で、前後が英字・数字・語をつなぐ記号でなく、さらに次のどちらかの形のものだけ
    /// (Haas は話した英字も小文字 1 文字で書き(「x軸」「変数xに」「n の」「m行 n列」)、日本語に付いた英字は話した語か断片か見分けられないので残す)。
    /// </summary>
    private static bool IsStray(string s, int i)
    {
        char c = s[i];
        if (c is < 'a' or > 'z') return false;
        if (i > 0 && (char.IsAsciiLetterOrDigit(s[i - 1]) || IsJoiner(s[i - 1]))) return false;
        if (i + 1 < s.Length && (char.IsAsciiLetterOrDigit(s[i + 1]) || IsJoiner(s[i + 1]))) return false;
        // 空白を飛ばした隣が英字・数字なら英文・単位の語
        int p = i - 1;
        while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
        if (p >= 0 && p < i - 1 && char.IsAsciiLetterOrDigit(s[p])) return false;
        int n = i + 1;
        while (n < s.Length && char.IsWhiteSpace(s[n])) n++;
        if (n < s.Length && n > i + 1 && char.IsAsciiLetterOrDigit(s[n])) return false;
        return IsEetoSpelling(s, i) || IsIsolated(s, p, n);
    }

    /// <summary>
    /// Haas が「ええと」(エーと)を「aと」と書いた形か。「a と」(英字と「と」の間に空白。「三部a と五分」)か、「aと」が文頭・句読点・空白・前の「aと」の後にあって
    /// 後ろも句読点・空白・文末・次の「aと」のとき(「aと、資料は」「aとaと、」)。「変数aと変数b」「a列とb列」「aとbを」「a と b」は話した語なので残す。
    /// </summary>
    private static bool IsEetoSpelling(string s, int i)
    {
        if (s[i] != 'a') return false;
        int t = i + 1;
        while (t < s.Length && char.IsWhiteSpace(s[t])) t++;
        if (t >= s.Length || s[t] != 'と') return false;
        int after = t + 1;
        while (after < s.Length && char.IsWhiteSpace(s[after])) after++;
        if (after < s.Length && char.IsAsciiLetterOrDigit(s[after]) && !IsEetoAt(s, after)) return false;
        if (t > i + 1) return true;
        bool before = i == 0 || char.IsWhiteSpace(s[i - 1]) || IsMark(s[i - 1]) || (i >= 2 && s[i - 1] == 'と' && s[i - 2] == 'a');
        bool behind = t + 1 == s.Length || char.IsWhiteSpace(s[t + 1]) || IsMark(s[t + 1]) || IsEetoAt(s, t + 1);
        return before && behind;
    }

    private static bool IsEetoAt(string s, int k) => k + 1 < s.Length && s[k] == 'a' && s[k + 1] == 'と';

    /// <summary>
    /// どの語にも付いていない英字か: 両隣(空白を飛ばす)が句読点か文頭・文末で、少なくとも片方が句読点(「h、資料は」「三部、a、五部」「送ります。a」)。
    /// 句読点の向こうも英字・数字なら並べた名前(「x、y、z」)なので残す。空白だけで区切られた英字(「n のデータ」「送ります h」)と、英字 1 文字だけの発話も残す。
    /// </summary>
    /// <param name="p">空白を飛ばした前の文字の位置(無ければ -1)。</param>
    /// <param name="n">空白を飛ばした後ろの文字の位置(無ければ s.Length)。</param>
    private static bool IsIsolated(string s, int p, int n)
    {
        bool prevMark = p >= 0 && IsMark(s[p]);
        bool nextMark = n < s.Length && IsMark(s[n]);
        if (!(p < 0 || prevMark) || !(n >= s.Length || nextMark) || !(prevMark || nextMark)) return false;
        if (prevMark)
        {
            int q = p - 1;
            while (q >= 0 && char.IsWhiteSpace(s[q])) q--;
            if (q >= 0 && char.IsAsciiLetterOrDigit(s[q])) return false;
        }
        if (nextMark)
        {
            int r = n + 1;
            while (r < s.Length && char.IsWhiteSpace(s[r])) r++;
            if (r < s.Length && char.IsAsciiLetterOrDigit(s[r])) return false;
        }
        return true;
    }

    private static bool IsJoiner(char c) => c is '.' or '-' or '_' or '/' or '@' or '\'' or '&' or '+' or '#' or ':' or '~' or '%' or '$';

    private static bool IsComma(char c) => c is '、' or ',' or '，';

    private static bool IsMark(char c) => c is '、' or '。' or ',' or '，' or '？' or '?' or '！' or '!';
}
