namespace Voice2Txt.Core;

/// <summary>whisper.cpp に渡す復号の設定(どのモデルでも同じ)。</summary>
public static class Decoding
{
    /// <summary>話す言葉。日本語固定(仕様 5 章 8)。</summary>
    public const string Language = "ja";

    /// <summary>
    /// 初期プロンプト。言い淀み(えーと・あの・ええと・その・まあ・えっと)をかなで書いた話し言葉を先に見せ、
    /// 言い淀みを英字(「ええと」→「A と」)や別の語に当てずに、かなのまま書かせる。
    /// 続けて数える列の終わり(ここのつ、10)を見せ、「…ここのつ、とお。」の「とお」(「つ」が付かない短い数)を「等」などの別の語でなく数として書かせる。
    /// 数字で見せるのは「とお」だけ: 「9つ」も数字で見せると、数え上げでない語の中の「いち」「ご」まで数字に寄る(「いちごいちえ」が「151へ」)。
    /// 末尾に句点を置かない: プロンプトが句点(「。」だけでも)で終わると、文頭の語の切れ目が変わり「いちごいちえの」が「一期一への」になる。
    /// 句点は前の文(言い淀みの列)の終わりに見せているので、届く文の句点は変わらない。
    /// 語の置換表・後処理ではない(届いた文字列は書き換えない)。短く保つ: 長いと無音・短い発話で幻聴の種になる(数える列を長く見せると物音が「1つ、10。」になる)。
    /// </summary>
    public const string InitialPrompt = "えーと、あの、ええと、その、まあ、えっと。ここのつ、10";

    /// <summary>whisper のエンコーダが 1 回に読む窓(30 秒)の長さ(audio_ctx。1 秒 = 50)。</summary>
    public const int FullAudioContext = 1500;

    /// <summary>速さを優先するときの窓の刻み(2 秒)。刻みごとに whisper.cpp の処理器を作って使い回す。</summary>
    public const int AudioContextStep = 100;

    /// <summary>速さを優先するときの窓の最短(10 秒)。短い窓(4 秒)では 3 秒の発話でも同じ文を繰り返して書き、かえって遅くなった。</summary>
    public const int MinAudioContext = 500;

    /// <summary>速さを優先するとき、窓を音声の長さの何倍にするか。音声の後ろに無音の窓が残らないと、復号が文の終わりを決められずに繰り返す。</summary>
    public const double AudioContextRatio = 1.5;

    /// <summary>速さを優先するとき、窓に足す余白(秒)。</summary>
    public const double AudioContextMarginSeconds = 2.0;

    /// <summary>
    /// 設定 preferSpeed がオンのときの窓(audio_ctx): 渡す音声の長さ × <see cref="AudioContextRatio"/> + 余白を刻みに切り上げる(最短 <see cref="MinAudioContext"/>)。
    /// 0 は窓を縮めない(30 秒のまま。whisper.cpp の既定)。30 秒の窓に届く長さの音声は縮めない(窓の外の音を読み落とさない)。
    /// エンコーダの処理量は窓の長さに比例するので、CPU で短い発話の処理が窓の比の分だけ縮む。
    /// </summary>
    public static int AudioContext(int samples, int sampleRate = Audio.SampleRate)
    {
        double sec = samples / (double)sampleRate * AudioContextRatio + AudioContextMarginSeconds;
        int ctx = (int)Math.Ceiling(sec * FullAudioContext / 30.0 / AudioContextStep) * AudioContextStep;
        return ctx >= FullAudioContext ? 0 : Math.Max(ctx, MinAudioContext);
    }

    /// <summary>
    /// 縮めた窓の復号が崩れた区切りか: それまでの文字列(<paramref name="previousText"/>。同じ発話の前の復号の分も含む)があり、
    /// 音声の終わり(+ <see cref="AudioContextMarginSeconds"/>)より後ろで終わる・長さが 0・それまでの文字列の末尾と同じ文字列(同じ文・句の繰り返し)のどれか。
    /// 最初の区切りは捨てない(縮めた窓では最初の区切りの終わりの時刻も音声より後ろになることがあり、捨てると発話が丸ごと消える)。
    /// 崩れたらその区切りから後ろは捨て、復号を止める(繰り返しの復号に時間を使わない)。
    /// </summary>
    public static bool Derailed(double audioSeconds, string? previousText, double start, double end, string text)
    {
        var prev = previousText?.Trim() ?? "";
        if (prev.Length == 0) return false;
        var t = text.Trim();
        return end > audioSeconds + AudioContextMarginSeconds || end <= start
            || t.Count(char.IsLetterOrDigit) >= 2 && prev.EndsWith(t, StringComparison.Ordinal);
    }

    /// <summary>
    /// 区切りの文字列の末尾で、文(句点・疑問符などで終わる 2 文字以上)がそのまま 2 回以上続いていれば 1 回分にする(縮めた窓の復号が 1 つの区切りの中で繰り返した)。
    /// 繰り返しが無ければそのまま返す。
    /// </summary>
    public static string TrimRepeatedTail(string text)
    {
        var t = text.TrimEnd();
        for (bool again = true; again;)
        {
            again = false;
            for (int n = t.Length / 2; n >= 2; n--)
            {
                if (!SentenceEnd(t[^1])) break;
                int a = t.Length - 2 * n;
                if (string.CompareOrdinal(t, a, t, t.Length - n, n) != 0) continue;
                var u = t.Substring(t.Length - n);
                // 繰り返しの単位は文・句の頭から(前が文頭・文の終わり・読点)。文字が 2 つ以上
                if (u.Count(char.IsLetterOrDigit) < 2 || a > 0 && !SentenceEnd(t[a - 1]) && t[a - 1] is not ('、' or ',' or '，') && !char.IsWhiteSpace(t[a - 1])) continue;
                t = t[..^n];
                again = true;
                break;
            }
        }
        return t.Length == text.TrimEnd().Length ? text : t;
    }

    private static bool SentenceEnd(char c) => c is '。' or '．' or '.' or '？' or '?' or '！' or '!';
}
