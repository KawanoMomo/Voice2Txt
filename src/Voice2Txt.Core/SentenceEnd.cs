namespace Voice2Txt.Core;

/// <summary>
/// 確定版の終わりに発話の区切りを付ける後処理。続けて話した発話を貼った文章で、どこが発話の境目かを確定版だけで読めるようにする。
/// 基準は whisper(large-v3-turbo)の確定版の付き方: 言い切った文(平叙文・「…ですか」の質問文)は「。」、途中で止めた発話(「…件なんですが」)は「、」。
/// Haas は文中の句点は書くが発話の終わりの句点をほとんど書かないので、whisper と同じ付き方に揃える(エンジンを問わず同じ規則を掛ける)。
/// 届ける直前に 1 回だけ掛ける(言い淀みの除去と空白の詰めの後。途中経過には掛けない)。
/// 既に句読点・閉じかっこ・記号で終わっていれば何もしない。終わりが日本語の文字でない(英数字)ときも何もしない(英文・ファイル名・数字の後に和文の約物を足さない)。
/// 言い切らない終わり(接続助詞「が」「けど」「ので」「て」、格助詞「は」「を」「に」「の」など)は「、」、それ以外は「。」。
/// </summary>
public static class SentenceEnd
{
    /// <summary>言い切らない終わり(途中で止めた発話)。長いものから比べる。</summary>
    public static readonly IReadOnlyList<string> Unfinished =
    [
        "けれども", "けれど", "ながら", "けど", "ので", "のに", "から", "ても", "でも", "たら", "なら", "とか", "しか", "より", "まで",
        "が", "て", "で", "し", "ば", "と", "は", "を", "に", "へ", "の", "も", "や",
    ];

    /// <summary>「は」で終わっても言い切った語(挨拶)。</summary>
    private static readonly string[] Complete = ["こんにちは", "こんばんは"];

    /// <summary>終わりに区切りを付けた文字列(付けなければ同じ文字列)。</summary>
    public static string Close(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text.TrimEnd();
        if (t.Length == 0) return text;
        char last = t[^1];
        if (IsClosed(last) || !IsJapaneseWord(last)) return text;
        return t + (IsUnfinished(t) ? "、" : "。");
    }

    /// <summary>言い切らない終わりか(終わりの語が <see cref="Unfinished"/> のどれか。挨拶は除く)。</summary>
    public static bool IsUnfinished(string t) =>
        !Complete.Any(t.EndsWith) && Unfinished.Any(w => t.EndsWith(w, StringComparison.Ordinal));

    /// <summary>句読点・閉じかっこ・記号で終わっているか(付け足さない)。</summary>
    private static bool IsClosed(char c) => c is '。' or '、' or '．' or '，' or '｡' or '､' or '.' or ',' or '！' or '？' or '!' or '?' or '…' or '‥'
        or '」' or '』' or '）' or ')' or '】' or '〕' or '〉' or '》' or '：' or ':' or '；' or ';' or '・' or '〜' or '～';

    /// <summary>かな・漢字・長音(和文の語の終わり)。全角英数・記号は含めない。</summary>
    private static bool IsJapaneseWord(char c) => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ' or '々'
        or >= '一' and <= '鿿' or >= '豈' and <= '﫿' or >= 'ｦ' and <= 'ﾟ';
}
