using System.Text;

namespace Voice2Txt.Core;

/// <summary>復号が返した語 1 つ。<see cref="AtSeconds"/> は復号に渡した音声の先頭からの秒(語の時刻を出せないエンジン・特殊な語は負)。</summary>
public readonly record struct TimedToken(double AtSeconds, string Text);

/// <summary>
/// 復号の 1 区切り(whisper の segment)。<see cref="Text"/> が正しい文字列。<see cref="Tokens"/> は語ごとの時刻で、
/// 語の文字列は <see cref="Text"/> と切れ目が合わないことがある(多バイト文字が 2 つの語に割れると、それぞれ U+FFFD になる)。
/// </summary>
public sealed record DecodedSegment(string Text, IReadOnlyList<TimedToken> Tokens);

/// <summary>拾い直しが使う復号の境界(本番は whisper.cpp、unit は偽物)。</summary>
public interface ISpanDecoder
{
    /// <summary>確定版の復号(日本語・初期プロンプト)。<see cref="Recovery.WindowSeconds"/> 秒以下の音声を渡す。</summary>
    Task<IReadOnlyList<DecodedSegment>> DecodeAsync(float[] samples, CancellationToken ct);

    /// <summary>拾い直しの復号(言語は自動判定・プロンプト無し・前の文脈無し)。文字列と、声が無い見込み(0〜1)。</summary>
    Task<(string Text, double NoSpeech)> DecodeSpanAsync(float[] samples, CancellationToken ct);
}

/// <summary>拾い直しを済ませた確定版の文字列と、拾い直して差し込んだ区間の数。</summary>
public sealed record RecoveredText(string Text, int Recovered);

/// <summary>
/// 拾い直し: 声があるのに確定版に文字が 1 つも無い区間を、その区間だけ文字起こしし直して確定版の該当位置へ差し込む。
/// 日本語に固定した復号は、発話の途中に挟まった別の言語の文(別の話者の英文など)を、エラーも無く丸ごと飛ばすことがある。
/// 声の区間は無音の間(<see cref="PauseSeconds"/> 秒以上)で分け、語の時刻(whisper.cpp の DTW)が 1 つも入らない区間を「文字が無い」とみなす。
/// 語の時刻を出せないエンジンでは何もしない(元の確定版のまま)。
/// </summary>
public static class Recovery
{
    /// <summary>1 回の復号に渡す最長(秒)。これより長い発話は声の間で切って順に復号する(whisper の窓は 30 秒)。</summary>
    public const double WindowSeconds = 29;

    /// <summary>声の区間を分ける無音の長さ(秒)。</summary>
    public const double PauseSeconds = 0.5;

    /// <summary>拾い直す区間の最短の長さ(秒)。咳・息・物音のような短い音は拾い直さない。</summary>
    public const double MinSpanSeconds = 0.5;

    /// <summary>拾い直す区間の前後に足して切り出す余白(秒)。</summary>
    public const double SpanPadSeconds = 0.2;

    /// <summary>語の時刻のずれの許し(秒)。区間の少し前・少し後ろに付いた語も、その区間の語とみなす。</summary>
    public const double TokenSlackSeconds = 0.3;

    /// <summary>拾い直した結果を捨てる、声が無い見込みの下限。</summary>
    public const double MaxNoSpeech = 0.6;

    /// <summary>
    /// 確定版を作る: <see cref="Windows"/> で切った順に復号してつなぎ、文字の無い声の区間を拾い直して差し込む。
    /// <paramref name="threshold"/> は無音のしきい値(設定値。声の区間は <see cref="Audio.VoiceLevel"/> で決める)。
    /// </summary>
    public static async Task<RecoveredText> TranscribeAsync(float[] samples, double threshold, ISpanDecoder decoder, CancellationToken ct)
    {
        int rate = Audio.SampleRate;
        var spans = VoicedSpans(samples, Audio.VoiceLevel(samples, threshold));
        var sb = new StringBuilder();
        var tokens = new List<(int Offset, double At, string Text)>();
        foreach (var (ws, we) in Windows(samples, spans))
        {
            var slice = ws == 0 && we == samples.Length ? samples : samples[ws..we];
            foreach (var seg in await decoder.DecodeAsync(slice, ct))
            {
                int baseOffset = sb.Length;
                var offsets = Align(seg.Text, seg.Tokens);
                for (int i = 0; i < seg.Tokens.Count; i++)
                {
                    var t = seg.Tokens[i];
                    tokens.Add((baseOffset + offsets[i], t.AtSeconds < 0 ? -1 : t.AtSeconds + (double)ws / rate, t.Text));
                }
                sb.Append(seg.Text);
            }
        }
        string text = sb.ToString();
        var words = tokens.Where(t => t.At >= 0 && IsWord(t.Text)).ToList();
        if (words.Count == 0) return new(text.Trim(), 0); // 語の時刻が無い(エンジンが出せない・文字が無い): 拾い直さない

        var inserts = new List<(int Offset, string Text)>();
        foreach (var (s, e) in spans)
        {
            double a = (double)s / rate, b = (double)e / rate;
            if (b - a < MinSpanSeconds) continue;
            if (words.Any(w => w.At >= a - TokenSlackSeconds && w.At <= b + TokenSlackSeconds)) continue;
            int pad = (int)(SpanPadSeconds * rate);
            int cs = Math.Max(0, s - pad), ce = Math.Min(samples.Length, e + pad);
            var cut = new float[Math.Max(ce - cs, (int)(Audio.MinDecodeSeconds * rate))];
            Array.Copy(samples, cs, cut, 0, ce - cs);
            var (got, noSpeech) = await decoder.DecodeSpanAsync(cut, ct);
            got = got.Trim();
            if (!Acceptable(got, noSpeech, text)) continue;
            // 差し込む位置: 区間より後ろで最初の語の前(区間の中に付いた句読点は前の文に残す)。後ろに語が無ければ末尾
            var next = words.FirstOrDefault(w => w.At >= a);
            int at = next.Text is null ? text.TrimEnd().Length : next.Offset;
            inserts.Add((at, got));
        }
        foreach (var (at, got) in inserts.OrderByDescending(x => x.Offset))
            text = text.Insert(at, Joint(text, at, got));
        return new(text.Trim(), inserts.Count);
    }

    /// <summary>
    /// 声の区間(サンプル位置の [開始, 終了))。30 ms 区間の RMS が <paramref name="level"/> 以上なら声とし、
    /// <see cref="PauseSeconds"/> 秒未満の無音はつなぐ。
    /// </summary>
    public static List<(int Start, int End)> VoicedSpans(float[] samples, double level, int sampleRate = Audio.SampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000);
        int gap = (int)Math.Ceiling(PauseSeconds * sampleRate / frame);
        var spans = new List<(int, int)>();
        int start = -1, last = -1;
        for (int f = 0; f * frame < samples.Length; f++)
        {
            int i = f * frame, end = Math.Min(samples.Length, i + frame);
            if (Audio.Rms(samples.AsSpan(i, end - i)) < level) continue;
            if (start < 0) start = f;
            else if (f - last - 1 >= gap) { spans.Add((start * frame, Math.Min(samples.Length, (last + 1) * frame))); start = f; }
            last = f;
        }
        if (start >= 0) spans.Add((start * frame, Math.Min(samples.Length, (last + 1) * frame)));
        return spans;
    }

    /// <summary>
    /// 復号に渡す区切り([開始, 終了))。<see cref="WindowSeconds"/> 秒以下なら全体を 1 つ。長ければ、窓に収まる最後の声の間の中ほどで切る。
    /// 間の無い長い声は、窓の後半で最も静かな 30 ms 区間で切る。
    /// </summary>
    public static List<(int Start, int End)> Windows(float[] samples, IReadOnlyList<(int Start, int End)> spans, int sampleRate = Audio.SampleRate)
    {
        int max = (int)(WindowSeconds * sampleRate);
        var res = new List<(int, int)>();
        int start = 0;
        while (samples.Length - start > max)
        {
            int limit = start + max, cut = -1;
            for (int i = 0; i + 1 < spans.Count; i++)
            {
                int mid = (spans[i].End + spans[i + 1].Start) / 2;
                if (mid > start && mid <= limit) cut = mid;
            }
            if (cut < 0) cut = QuietestCut(samples, start + max / 2, limit, sampleRate);
            res.Add((start, cut));
            start = cut;
        }
        res.Add((start, samples.Length));
        return res;
    }

    private static int QuietestCut(float[] samples, int from, int to, int sampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000), best = to;
        double low = double.MaxValue;
        for (int i = from; i + frame <= to; i += frame)
        {
            double r = Audio.Rms(samples.AsSpan(i, frame));
            if (r < low) { low = r; best = i + frame / 2; }
        }
        return best;
    }

    /// <summary>
    /// 語ごとに、区切りの文字列 <paramref name="text"/> の中での開始位置を返す。語の文字列が見つかればその位置、
    /// 割れた多バイト文字(U+FFFD)・特殊な語(<c>[_BEG_]</c> 等)は直前の語の終わり。
    /// </summary>
    public static int[] Align(string text, IReadOnlyList<TimedToken> tokens)
    {
        var res = new int[tokens.Count];
        int p = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i].Text ?? "";
            res[i] = p;
            if (IsSpecial(t) || t.Contains('�') || t.Length == 0) continue;
            int at = text.IndexOf(t, p, StringComparison.Ordinal);
            if (at < 0 && t.Trim().Length > 0) { t = t.Trim(); at = text.IndexOf(t, p, StringComparison.Ordinal); }
            // 見つからない・遠すぎる(後ろの同じ文字に当たった)なら位置は p のまま。割れた文字の並びの直後の語なら、割れた文字は p から at の手前まで
            if (at < 0 || at - p > MaxAlignSkip) continue;
            res[i] = at;
            p = at + t.Length;
        }
        return res;
    }

    /// <summary>語の文字列を探すとき、直前の語の終わりから読み飛ばしてよい文字数(割れた多バイト文字の分)。</summary>
    private const int MaxAlignSkip = 8;

    /// <summary>文字を持つ語か(句読点・空白・特殊な語は数えない。割れた多バイト文字は数える)。</summary>
    public static bool IsWord(string? t) =>
        !string.IsNullOrEmpty(t) && !IsSpecial(t) && (t.Contains('�') || t.Any(char.IsLetterOrDigit));

    private static bool IsSpecial(string t) => t.StartsWith("[_", StringComparison.Ordinal) && t.EndsWith(']');

    /// <summary>
    /// 拾い直した文字列を差し込んでよいか: 文字が 2 つ以上あり、声が無い見込みが低く、音の注記(「(拍手)」「[音楽]」「♪」)でなく、
    /// 確定版に既に入っていない(句読点・空白・大文字小文字を除いて比べる)。
    /// </summary>
    public static bool Acceptable(string got, double noSpeech, string text)
    {
        if (got.Count(char.IsLetterOrDigit) < 2) return false;
        if (noSpeech > MaxNoSpeech) return false;
        if ("([（［【♪*".Contains(got[0]) || ")]）］】♪*".Contains(got[^1])) return false;
        return !Normalize(text).Contains(Normalize(got), StringComparison.Ordinal);
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>差し込む文字列の前後の空白: 英字・数字どうしが接する所だけ空白を入れる(日本語の文とは詰める)。</summary>
    private static string Joint(string text, int at, string got)
    {
        static bool Latin(char c) => c < 0x80 && (char.IsLetterOrDigit(c) || c is '.' or ',' or '!' or '?');
        string s = got;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]) && Latin(text[at - 1]) && Latin(got[0])) s = " " + s;
        if (at < text.Length && !char.IsWhiteSpace(text[at]) && Latin(text[at]) && Latin(got[^1])) s += " ";
        return s;
    }
}
