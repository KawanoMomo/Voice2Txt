using System.Text;

namespace Voice2Txt.Core;

/// <summary>復号が返した語 1 つ。<see cref="AtSeconds"/> は復号に渡した音声の先頭からの秒(語の時刻を出せないエンジン・特殊な語は負)。</summary>
public readonly record struct TimedToken(double AtSeconds, string Text);

/// <summary>
/// 復号の 1 区切り(whisper の segment)。<see cref="Text"/> が正しい文字列。<see cref="Tokens"/> は語ごとの時刻で、
/// 語の文字列は <see cref="Text"/> と切れ目が合わないことがある(多バイト文字が 2 つの語に割れると、それぞれ U+FFFD になる)。
/// <see cref="EndSeconds"/> は区切りの終わり(渡した音声の先頭からの秒)。
/// </summary>
public sealed record DecodedSegment(string Text, IReadOnlyList<TimedToken> Tokens, double EndSeconds = 0);

/// <summary>拾い直しが使う復号の境界(本番は whisper.cpp、unit は偽物)。</summary>
public interface ISpanDecoder
{
    /// <summary>
    /// 確定版の復号(日本語・初期プロンプト)。音声の途中で返し終えることがある(DTW を有効にした whisper.cpp は窓を送る途中で止まる)。
    /// 続きは <see cref="Recovery.TranscribeAsync"/> が最後の区切りの終わりから渡し直す。
    /// </summary>
    Task<IReadOnlyList<DecodedSegment>> DecodeAsync(float[] samples, CancellationToken ct);

    /// <summary>拾い直しの復号(言語は自動判定・プロンプト無し・前の文脈無し)。文字列と、声が無い見込み(0〜1)。</summary>
    Task<(string Text, double NoSpeech)> DecodeSpanAsync(float[] samples, CancellationToken ct);
}

/// <summary>
/// 拾い直しを済ませた確定版の文字列と、拾い直して差し込んだ区間の数(うち <see cref="Reused"/> は前の結果を使い回した数、
/// <see cref="Holes"/> は語の穴(声の区間の中で語の時刻が空いた所)に差し込んだ数)。
/// </summary>
public sealed record RecoveredText(string Text, int Recovered, int Reused = 0, int Holes = 0);

/// <summary>
/// 拾い直しの復号の結果を、切り出した音声ごとに覚えておく。押下中の途中経過で拾い直した区間は、離した後の確定版でも同じ音で切り出されるので、
/// 2 回目の復号をせずに前の結果を使う(離してから届くまでを延ばさない)。音が 1 サンプルでも違えば使わない(中身で照合する)。
/// 古いものから忘れる。スレッドセーフ。
/// </summary>
public sealed class SpanCache(int capacity = SpanCache.DefaultCapacity)
{
    /// <summary>覚えておく区間の数の既定(1 つの発話の拾い直しは数区間。数秒の音声 × この数だけメモリを持つ)。</summary>
    public const int DefaultCapacity = 16;

    private readonly LinkedList<(float[] Samples, (string Text, double NoSpeech) Result)> _items = new();

    public bool TryGet(float[] samples, out (string Text, double NoSpeech) result)
    {
        lock (_items)
        {
            for (var n = _items.First; n is not null; n = n.Next)
            {
                if (!n.Value.Samples.AsSpan().SequenceEqual(samples)) continue;
                _items.Remove(n); _items.AddFirst(n);
                result = n.Value.Result;
                return true;
            }
        }
        result = default;
        return false;
    }

    public void Add(float[] samples, (string Text, double NoSpeech) result)
    {
        lock (_items)
        {
            _items.AddFirst(((float[])samples.Clone(), result));
            while (_items.Count > Math.Max(1, capacity)) _items.RemoveLast();
        }
    }
}

/// <summary>
/// 拾い直し: 声があるのに確定版に文字が 1 つも無い区間を、その区間だけ文字起こしし直して確定版の該当位置へ差し込む。
/// 日本語に固定した復号は、発話の途中に挟まった別の言語の文(別の話者の英文など)を、エラーも無く丸ごと飛ばすことがある。
/// 声の区間は無音の間(<see cref="PauseSeconds"/> 秒以上)で分け、語の時刻(whisper.cpp の DTW)が 1 つも入らない区間を「文字が無い」とみなす。
/// 語の時刻を出せないエンジンでは何もしない(元の確定版のまま)。
/// </summary>
public static class Recovery
{
    /// <summary>復号が返し終えた所から後ろにこれ以上の音声が残っていれば、続きを復号する(whisper.cpp 自身も残り 1 秒未満で止める)。</summary>
    public const double ContinueSeconds = 1.0;

    /// <summary>続きを復号するのは、返し終えた所(より少し前でもよい: この秒数まで)から始まる声の区間があるときだけ。</summary>
    public const double ContinueSlackSeconds = 0.1;

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
    /// 語の穴: 隣り合う 2 つの語の時刻の間に、声(<see cref="Audio.VoiceLevel"/> 以上の 30 ms 区間)がこの秒数以上あれば、間の語を飛ばしたとみなす。
    /// 声の区間が無音の間で分かれていない所(読み上げた記号の語「アンダーバー、最終、ドット」を復号が「report.pdf」にまとめた)も拾い直す。
    /// 普通の発話の語の間は 0.9 秒以下(前の語の読みの長さを含む。ゆっくり読んだ数字で 0.96 秒)、雑音の混じる録音で 1.4 秒未満。
    /// 雑音の所を拾い直しても、日本語の文字が無い結果(「Two.」)は差し込まない(<see cref="HasJapanese"/>)。
    /// </summary>
    public const double HoleSeconds = 1.3;

    /// <summary>
    /// 確定版を作る: 全体を復号し、復号が途中で返し終えたら最後の区切りの終わりから続きを復号してつなぐ(whisper.cpp が窓を送るのと同じ位置)。
    /// その後、文字の無い声の区間を拾い直して差し込む。声の区間の中で語の時刻が <see cref="HoleSeconds"/> 秒以上の声をまたいで空いた所(語の穴)も
    /// 前後の語の時刻の間を切り出して拾い直し、後ろの語の前に差し込む(日本語の文字を含む結果だけ。途中経過では終わった穴だけ)。
    /// <paramref name="threshold"/> は無音のしきい値(設定値。声の区間は <see cref="Audio.VoiceLevel"/> で決める)。
    /// <paramref name="cache"/> があれば、同じ音で切り出した区間は前の拾い直しの結果を使う(途中経過で済ませた分を確定版で復号し直さない)。
    /// <paramref name="prefetch"/>(途中経過のときだけ真)なら、語が英字に読めた終わった区間も拾い直して <paramref name="cache"/> に覚えておく(差し込まない)。
    /// </summary>
    public static async Task<RecoveredText> TranscribeAsync(float[] samples, double threshold, ISpanDecoder decoder, CancellationToken ct, SpanCache? cache = null, bool prefetch = false)
    {
        int rate = Audio.SampleRate;
        double level = Audio.VoiceLevel(samples, threshold);
        var spans = VoicedSpans(samples, level);
        var sb = new StringBuilder();
        var tokens = new List<(int Offset, double At, string Text)>();
        int pos = 0;
        while (true)
        {
            var slice = pos == 0 ? samples : samples[pos..];
            double end = 0;
            foreach (var seg in await decoder.DecodeAsync(slice, ct))
            {
                int baseOffset = sb.Length;
                var offsets = Align(seg.Text, seg.Tokens);
                for (int i = 0; i < seg.Tokens.Count; i++)
                {
                    var t = seg.Tokens[i];
                    tokens.Add((baseOffset + offsets[i], t.AtSeconds < 0 ? -1 : t.AtSeconds + (double)pos / rate, t.Text));
                }
                sb.Append(seg.Text);
                end = Math.Max(end, seg.EndSeconds);
            }
            int next = pos + (int)(end * rate);
            // 返し終えた・進まない・残りが短いなら続けない。返し終えた所より後ろで始まる声の区間が無ければ(話し終えた後の息・物音・音楽の続きだけ)、
            // whisper.cpp 自身もそこで止める(続けると「ご視聴ありがとうございました」のような音声に無い文が付く)
            if (next <= pos || samples.Length - next < ContinueSeconds * rate) break;
            int slack = (int)(ContinueSlackSeconds * rate);
            if (!spans.Any(s => s.Start >= next - slack && s.End - s.Start >= MinSpanSeconds * rate)) break;
            pos = next;
        }
        string text = sb.ToString();
        var words = tokens.Where(t => t.At >= 0 && IsWord(t.Text)).ToList();
        if (words.Count == 0) return new(text.Trim(), 0); // 語の時刻が無い(エンジンが出せない・文字が無い): 拾い直さない

        var inserts = new List<(int Offset, string Text, bool Hole)>();
        var wordless = new List<(int Start, int End)>();
        int reused = 0;
        foreach (var (s, e) in spans)
        {
            double a = (double)s / rate, b = (double)e / rate;
            if (b - a < MinSpanSeconds) continue;
            var inSpan = words.Where(w => w.At >= a - TokenSlackSeconds && w.At <= b + TokenSlackSeconds).ToList();
            int pad = (int)(SpanPadSeconds * rate);
            int cs = Math.Max(0, s - pad), ce = Math.Min(samples.Length, e + pad);
            float[] Cut()
            {
                var c = new float[Math.Max(ce - cs, (int)(Audio.MinDecodeSeconds * rate))];
                Array.Copy(samples, cs, c, 0, ce - cs);
                return c;
            }
            // 止められた(途中経過が離しで打ち切られた)なら次の復号を始めない: 確定版をエンジンの空きで待たせない
            ct.ThrowIfCancellationRequested();
            if (inSpan.Count > 0)
            {
                // 途中経過: 英字に読めた区間は、確定版(前後に日本語が続く)の復号が飛ばすことがある。終わった区間なら先に拾い直して覚えておく
                if (prefetch && cache is not null && samples.Length - e >= PauseSeconds * rate && MostlyLatin(inSpan.Select(w => w.Text)))
                {
                    var c = Cut();
                    if (!cache.TryGet(c, out _)) cache.Add(c, await decoder.DecodeSpanAsync(c, ct));
                }
                continue;
            }
            wordless.Add((s, e));
            var cut = Cut();
            bool hit = false;
            (string Text, double NoSpeech) res;
            if (cache is not null && cache.TryGet(cut, out res)) hit = true;
            else
            {
                res = await decoder.DecodeSpanAsync(cut, ct);
                cache?.Add(cut, res);
            }
            var (got, noSpeech) = res;
            got = got.Trim();
            if (!Acceptable(got, noSpeech, text)) continue;
            if (hit) reused++;
            // 差し込む位置: 区間より後ろで最初の語の前(区間の中に付いた句読点は前の文に残す)。後ろに語が無ければ末尾
            var next = words.FirstOrDefault(w => w.At >= a);
            int at = next.Text is null ? text.TrimEnd().Length : next.Offset;
            inserts.Add((at, got, false));
        }
        int holes = 0;
        // 語の穴: 声の区間の中で、隣り合う語の時刻の間に声が長く続く所
        for (int i = 1; i < words.Count; i++)
        {
            double a = words[i - 1].At, b = words[i].At;
            if (VoicedSeconds(samples, level, a, b) < HoleSeconds) continue;
            // 語の無い声の区間が入っていれば、上で区間ごと拾い直した(同じ音を 2 回差し込まない)
            if (wordless.Any(w => w.Start < b * rate && w.End > a * rate)) continue;
            int cs = (int)(a * rate), ce = Math.Min(samples.Length, (int)(b * rate));
            // 途中経過: 後ろの語が写しの終わり近く(まだ話している所)なら待つ。終わった穴は拾い直して覚えておき、確定版が同じ音で切り出せば復号し直さない
            if (prefetch && samples.Length - ce < PauseSeconds * rate) continue;
            ct.ThrowIfCancellationRequested();
            var c = new float[Math.Max(ce - cs, (int)(Audio.MinDecodeSeconds * rate))];
            Array.Copy(samples, cs, c, 0, ce - cs);
            bool hit = false;
            (string Text, double NoSpeech) res;
            if (cache is not null && cache.TryGet(c, out res)) hit = true;
            else
            {
                res = await decoder.DecodeSpanAsync(c, ct);
                cache?.Add(c, res);
            }
            var (got, noSpeech) = res;
            // 切り出しは前後の語の時刻までなので、前後の語の読みが付いてくる(「underbar最終.p」の「p」)。確定版と重なる所は落とす
            int at = words[i].Offset;
            got = TrimOverlap(text, at, got.Trim());
            if (!HasJapanese(got) || !Acceptable(got, noSpeech, text)) continue;
            if (hit) reused++;
            inserts.Add((at, got, true));
            holes++;
        }
        foreach (var (at, got, hole) in inserts.OrderByDescending(x => x.Offset))
            text = text.Insert(at, Joint(text, at, got, hole));
        return new(text.Trim(), inserts.Count, reused, holes);
    }

    /// <summary>[<paramref name="from"/>, <paramref name="to"/>) 秒のうち、30 ms 区間の RMS が <paramref name="level"/> 以上の長さ(秒)。</summary>
    public static double VoicedSeconds(float[] samples, double level, double from, double to, int sampleRate = Audio.SampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000), n = 0;
        int end = Math.Min(samples.Length, (int)(to * sampleRate));
        for (int i = Math.Max(0, (int)(from * sampleRate)) / frame * frame; i + frame <= end; i += frame)
            if (Audio.Rms(samples.AsSpan(i, frame)) >= level) n++;
        return n * frame / (double)sampleRate;
    }

    /// <summary>ひらがな・カタカナ・漢字を含むか(日本語の発話の穴から拾い直した結果か。雑音を読んだ「Two.」「Ому」は含まない)。</summary>
    public static bool HasJapanese(string s) =>
        s.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or '々' or >= 'ｦ' and <= 'ﾟ');

    /// <summary>
    /// 差し込む文字列 <paramref name="got"/> の末尾が <paramref name="text"/> の <paramref name="at"/> からと重なれば、その分を落とす。
    /// 先頭が <paramref name="at"/> の手前と重なっても同じ(大文字小文字は区別しない)。
    /// </summary>
    public static string TrimOverlap(string text, int at, string got)
    {
        for (int k = Math.Min(got.Length, text.Length - at); k > 0; k--)
            if (string.Compare(text, at, got, got.Length - k, k, StringComparison.OrdinalIgnoreCase) == 0) { got = got[..^k]; break; }
        for (int k = Math.Min(got.Length, at); k > 0; k--)
            if (string.Compare(text, at - k, got, 0, k, StringComparison.OrdinalIgnoreCase) == 0) { got = got[k..]; break; }
        return got.Trim();
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

    /// <summary>語の文字の半分以上が英字か(日本語に固定した復号が、別の言語の区間を英字のまま書いた)。</summary>
    public static bool MostlyLatin(IEnumerable<string> words)
    {
        int latin = 0, all = 0;
        foreach (var c in words.SelectMany(w => w))
        {
            if (!char.IsLetter(c) && c != '�') continue;
            all++;
            if (c < 0x80) latin++;
        }
        return all > 0 && latin * 2 >= all;
    }

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

    /// <summary>
    /// 差し込む文字列の前後の空白: 英字・数字どうしが接する所だけ空白を入れる(日本語の文とは詰める)。
    /// 文の区間は英文の句読点の後ろも空ける。語の穴(<paramref name="inWord"/>)は語の続きなので、句読点の前後は詰める(「report.underbar最終.pdf」)。
    /// </summary>
    private static string Joint(string text, int at, string got, bool inWord = false)
    {
        bool Latin(char c) => c < 0x80 && (char.IsLetterOrDigit(c) || (!inWord && c is '.' or ',' or '!' or '?'));
        string s = got;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]) && Latin(text[at - 1]) && Latin(got[0])) s = " " + s;
        if (at < text.Length && !char.IsWhiteSpace(text[at]) && Latin(text[at]) && Latin(got[^1])) s += " ";
        return s;
    }
}
