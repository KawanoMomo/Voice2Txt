using System.Text;

namespace Voice2Txt.Core;

/// <summary>
/// 確定版から言い淀み(「ええ」「ああ」「えっと」など)を取り除く後処理。届ける直前に 1 回だけ掛ける(途中経過・ログには関係しない)。
/// 取り除くのは語が区切り(文頭・句読点・空白)から始まる所だけ。
/// 2 文字の語(「あの」「その」「まあ」「ええ」など、指示語や返事と同じ形のもの)は、後ろも区切り(読点・句点・空白・文末)のときだけ取り除く
/// (「あの人」「そのため」「ああいう」は残る)。3 文字以上の語(「ええと」「えっと」「うーん」など)は後ろに続く語があっても取り除く。
/// 語の後ろの伸ばし(ー・〜)と読点は一緒に消す。カタカナで書かれた言い淀みも同じ語として扱う。
/// </summary>
public static class Fillers
{
    /// <summary>既定で取り除く語(日本語の代表的な言い淀み)。設定 <see cref="AppSettings.Fillers"/> の初期値。</summary>
    public static readonly IReadOnlyList<string> DefaultWords =
    [
        "えーっと", "ええっと", "えーと", "ええと", "えっと", "あのう", "うーん",
        "ええ", "えー", "ああ", "あー", "あの", "その", "まあ", "まー", "んー",
    ];

    /// <summary>言い淀みを取り除いた文字列と、取り除いた数。語が空なら何もしない。</summary>
    public static (string Text, int Removed) Remove(string text, IReadOnlyList<string> words)
    {
        if (string.IsNullOrEmpty(text) || words.Count == 0) return (text, 0);
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => Fold(w.Trim()))
            .Distinct().OrderByDescending(w => w.Length).ToArray();
        if (list.Length == 0) return (text, 0);

        var sb = new StringBuilder(text.Length);
        int removed = 0, i = 0;
        while (i < text.Length)
        {
            int end = i == 0 || IsBreak(text[i - 1]) ? MatchAt(text, i, list) : -1;
            if (end < 0) { sb.Append(text[i]); i++; continue; }
            removed++;
            i = end;
        }
        if (removed == 0) return (text, 0);
        return (Tidy(sb.ToString()), removed);
    }

    /// <summary>i から言い淀みが始まっていれば、消す範囲の終わり(伸ばし・読点・空白を含む)。無ければ -1。</summary>
    private static int MatchAt(string s, int i, string[] words)
    {
        foreach (var w in words)
        {
            if (!StartsWithFolded(s, i, w)) continue;
            int j = i + w.Length;
            while (j < s.Length && IsLong(s[j])) j++;
            if (j == s.Length) return j;
            if (IsComma(s[j]) || char.IsWhiteSpace(s[j]))
            {
                while (j < s.Length && (IsComma(s[j]) || char.IsWhiteSpace(s[j]))) j++;
                return j;
            }
            if (IsStop(s[j]))
            {
                // 文がこの語だけ(「ええ。」)なら句点ごと消す。前に読点があれば句点は残す(後で「、。」を「。」に詰める)
                bool alone = i == 0 || IsStop(s[i - 1]) || char.IsWhiteSpace(s[i - 1]);
                if (!alone) return j;
                j++;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                return j;
            }
            if (w.Length >= 3) return j;
            // 2 文字の語に別の語が続く(「あの人」「そのため」)— 言い淀みではない。短い語も試す
        }
        return -1;
    }

    /// <summary>取り除いた跡を整える: 先頭の読点・句点、「、。」、続いた読点、末尾の読点。</summary>
    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (sb.Length == 0 && (IsComma(c) || IsStop(c) || char.IsWhiteSpace(c))) continue;
            if (sb.Length > 0 && IsComma(sb[^1]) && (IsComma(c) || IsStop(c))) sb.Length--;
            sb.Append(c);
        }
        while (sb.Length > 0 && (IsComma(sb[^1]) || char.IsWhiteSpace(sb[^1]))) sb.Length--;
        // 句読点しか残らなければ空(取り消しになる)
        return sb.ToString().Any(c => !IsComma(c) && !IsStop(c) && !char.IsWhiteSpace(c)) ? sb.ToString() : "";
    }

    private static bool StartsWithFolded(string s, int i, string w)
    {
        if (i + w.Length > s.Length) return false;
        for (int k = 0; k < w.Length; k++)
            if (Fold(s[i + k]) != w[k]) return false;
        return true;
    }

    private static string Fold(string w) => string.Concat(w.Select(Fold));

    /// <summary>カタカナをひらがなに、半角の長音を全角に寄せる(比べるときだけ)。</summary>
    private static char Fold(char c) => c switch
    {
        >= 'ァ' and <= 'ヶ' => (char)(c - 0x60),
        'ｰ' => 'ー',
        _ => c,
    };

    private static bool IsLong(char c) => c is 'ー' or 'ｰ' or '〜' or '～' or '~';
    private static bool IsComma(char c) => c is '、' or ',' or '，' or '､';
    private static bool IsStop(char c) => c is '。' or '.' or '．' or '｡' or '!' or '?' or '！' or '？' or '…';
    private static bool IsBreak(char c) => IsComma(c) || IsStop(c) || char.IsWhiteSpace(c) || c is '「' or '」' or '『' or '』' or '(' or ')' or '（' or '）';
}
