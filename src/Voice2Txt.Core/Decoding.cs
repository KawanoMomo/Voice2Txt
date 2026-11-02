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
}
