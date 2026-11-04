using System.Text;

namespace Voice2Txt.Core;

/// <summary>
/// カスタム語彙(設定 keywords)の表記へ、whisper の復号そのものを寄せる(語彙の当て方。初期プロンプト <see cref="Decoding.PromptFor"/> と併せて使う)。
/// 初期プロンプトは文脈として読まれるだけで、名詞(「即達」→「速達」)は寄るが、動詞の活用形(「銭湯に酔って」→「寄って」)は表記を見せても変わらない。
/// そこで復号の 1 手ごとに、語彙の表記の続きになる語(token)の点(logit)に <see cref="Bonus"/> を足す(書きかけの語彙があればその続き、無ければ語彙の頭)。
/// 足すのは、モデルが次に文字(かな・漢字・英数)を書こうとしているときだけ: 句読点・区切り・終わりの位置は動かさず、文字を別の文字に置き換えるだけにする。
/// 足す語は文字の切れ目で終わるものだけ(漢字の頭のバイトだけの語に足すと、同じ頭の別の漢字まで寄る)。
/// 寄せた結果は、寄せない復号と比べて採るかを決める(<see cref="Accept"/>): 寄せて 1 手でも選ぶ語が変わったら、同じ音声を寄せずに復号し直し、
/// 変わった所がすべて語彙の表記の丸ごとの中にあり、かつ寄せた分を除いた文の確からしさが寄せない復号より下がらないときだけ寄せた方を採る。
/// 文の読み(<see cref="Reading"/>。Windows の日本語 IME が返すかな)が分かるときは、寄せる点を <see cref="ReadingBonus"/> に上げ、
/// 寄せた文と寄せない文の読みが同じ(<see cref="SoundsAlike"/>。同音の書き分けだけ)ときに限って採る: 既定の Whisper が強く信じる同音の語
/// (「週末に来るまで」の「来」は「車」より 6〜7 だけ上)は <see cref="Bonus"/> では寄らず、点を上げると音の違う語まで寄るので、音で確かめる。
/// 語の置換表・後処理ではない(どの語をどう書くかはモデルが音声から決める。届いた文字列は書き換えない)。
/// 1 つの処理器の復号は直列(<see cref="Begin"/> から次の <see cref="Begin"/> まで 1 回の復号)。
/// </summary>
public sealed class KeywordBias
{
    /// <summary>
    /// 語彙の続きになる語に足す点。合成音声と transcriber のコーパス(397 本)で、「銭湯に寄って」は 2.25 から寄り(2 では「酔って」のまま)、
    /// 3 では語彙と無関係な語の置き換え(語彙の頭の 1 文字だけが入る)が増えるが、それは <see cref="Accept"/> が落とす。余裕を取って 3。
    /// </summary>
    public const float Bonus = 3f;

    /// <summary>
    /// 文の読みが分かるとき(<see cref="Reading"/>)に足す点。「週末に来るまで」→「車で」は「来」と「車」の差が 6.5〜7 で、8 から寄る。
    /// 音の違う語に寄ったもの(読みが違う)は <see cref="SoundsAlike"/> が落とす。
    /// </summary>
    public const float ReadingBonus = 8f;

    /// <summary>寄せた分を除いた文の確からしさ(自然対数)が、寄せない復号よりどれだけ下がるまで許すか。</summary>
    public const double Slack = 0.5;

    private static readonly Encoding Bytes = Encoding.Latin1; // バイト列をそのまま 1 文字ずつの文字列にする(辞書の鍵)
    private readonly byte[][] _words;
    private byte[][]? _vocab;
    private Dictionary<string, int>? _byText;
    private int _eot;
    private float[]? _prevLogp;
    private int _prevN = -1;
    private readonly List<byte> _decoded = new();
    private readonly HashSet<int> _boosted = new();

    /// <summary>語彙の表記(<see cref="Decoding.KeywordWords"/>)。</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>文の読み(かな)を返す(分からなければ null)。null なら読みで確かめず、点は <see cref="Bonus"/>。</summary>
    public Func<string, string?>? Reading { get; }

    /// <summary>語彙の続きになる語に足す点(読みで確かめられるなら <see cref="ReadingBonus"/>)。</summary>
    public float Boost => Reading is null ? Bonus : ReadingBonus;

    public KeywordBias(IEnumerable<string>? keywords, Func<string, string?>? reading = null)
    {
        Words = Decoding.KeywordWords(keywords);
        _words = Words.Select(Encoding.UTF8.GetBytes).ToArray();
        Reading = reading;
    }

    /// <summary>語彙が無い(寄せない。復号は今までどおり 1 回)。</summary>
    public bool IsEmpty => _words.Length == 0;

    /// <summary>語の一覧を受け取った(最初の復号で whisper から読む)。</summary>
    public bool HasVocabulary => _vocab is not null;

    /// <summary>この復号で寄せるか。</summary>
    public bool On { get; private set; }

    /// <summary>この復号で、寄せたために選ぶ語が変わった手があった。</summary>
    public bool Flipped { get; private set; }

    /// <summary>この復号は 1 本の語の列として点を数えられなかった(温度を上げたやり直しで複数の列を並べて復号した等)。</summary>
    public bool Unreliable { get; private set; }

    /// <summary>この復号で選んだ語の、寄せる前の対数確率の和。</summary>
    public double Score { get; private set; }

    /// <summary>寄せて語が変わった手で、寄せる前の最良の語との対数確率の差の和(寄せた分)。</summary>
    public double Gap { get; private set; }

    /// <summary>語の一覧(語の番号 → その語のバイト列)と、文の終わりの語の番号(これより後ろは時刻などの特別な語)。</summary>
    public void SetVocabulary(IReadOnlyList<byte[]> tokens, int eot)
    {
        _vocab = tokens.ToArray();
        _eot = eot;
        _byText = new Dictionary<string, int>();
        for (int i = 0; i < Math.Min(eot, _vocab.Length); i++) _byText.TryAdd(Bytes.GetString(_vocab[i]), i);
    }

    /// <summary>次の復号を始める。<paramref name="on"/> が偽なら寄せずに点だけ数える(読みで確かめるときは、寄せられる手 <see cref="Candidates"/> も集める)。</summary>
    public void Begin(bool on) => Start(on && !IsEmpty, []);

    /// <summary>
    /// 読みで確かめるときの寄せた復号を始める: <paramref name="force"/> の手(寄せない復号の <see cref="Candidates"/> から選ぶ)でだけ語彙の頭を選ばせ、
    /// その後は書きかけの語彙の続きに <see cref="Boost"/> を足して語彙を書き切らせる。ほかの手には足さない(点が大きいので、どこにでも足すと文が崩れる)。
    /// </summary>
    public void BeginForced(IEnumerable<Candidate> force) => Start(false, force.Select(c => (c.Window, c.Step)).ToHashSet());

    private void Start(bool on, HashSet<(int, int)> force)
    {
        On = on;
        _force = force;
        _lastForce = force.Count == 0 ? (-1, -1) : force.Max();
        Flipped = Unreliable = false;
        Score = Gap = 0;
        _prevN = -1;
        _window = -1;
        _candidates.Clear();
        _sequence.Clear();
    }

    /// <summary>この復号の結果(<see cref="Accept"/> に渡す)。</summary>
    public DecodeScore Result => new(Flipped, Unreliable, Score, Gap);

    /// <summary>
    /// 読みで確かめるとき(<see cref="Reading"/>)、寄せずに(または <see cref="BeginForced"/> の最後の手より後で)復号した列で、語彙の頭が最良の語から
    /// <see cref="Boost"/> 未満の差にあった手(差の小さい順)。点を数えられない復号では空。
    /// </summary>
    public IReadOnlyList<Candidate> Candidates => Unreliable || Reading is null ? [] : _candidates.OrderBy(c => c.Gap).ToList();

    private readonly List<Candidate> _candidates = new();
    private HashSet<(int, int)> _force = [];
    private (int, int) _lastForce = (-1, -1);
    private int _window = -1;

    /// <summary>
    /// 復号の 1 手(whisper.cpp の logits filter)。<paramref name="tokens"/> はこの列でここまでに選んだ語、<paramref name="logits"/> は次の語の点(寄せるならここに足す)。
    /// </summary>
    public void OnLogits(ReadOnlySpan<int> tokens, Span<float> logits)
    {
        if (_vocab is null) return;
        int n = tokens.Length, nv = Math.Min(logits.Length, _vocab.Length);
        if (n == 0) _window++; // 30 秒の窓ごとに列は頭から
        // 前の手の点: 列が 1 手伸びていれば、前の手で選んだ語の対数確率を足す(伸び方が違えば別の列が混ざっている)
        if (_prevLogp is not null && _prevN >= 0)
        {
            if (n == _prevN + 1 && n > 0 && tokens[n - 1] < _prevLogp.Length) Score += _prevLogp[tokens[n - 1]];
            else if (n != 0) Unreliable = true;
        }
        int best = 0;
        float max = float.NegativeInfinity;
        for (int i = 0; i < nv; i++) if (logits[i] > max) { max = logits[i]; best = i; }
        double sum = 0;
        for (int i = 0; i < nv; i++) if (!float.IsNegativeInfinity(logits[i])) sum += Math.Exp(logits[i] - max);
        float lse = max + (float)Math.Log(sum);
        if (_prevLogp is null || _prevLogp.Length != nv) _prevLogp = new float[nv];
        for (int i = 0; i < nv; i++) _prevLogp[i] = logits[i] - lse;
        _prevN = n;
        if (_window >= 0) _sequence[_window] = tokens.ToArray();
        bool scan = !On && Reading is not null && !IsEmpty;
        if (!(On || scan) || best >= _eot || !StartsLetter(_vocab[best])) return;

        _decoded.Clear();
        foreach (var t in tokens) if (t >= 0 && t < _eot && t < _vocab.Length) _decoded.AddRange(_vocab[t]);
        var d = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_decoded);
        _boosted.Clear();
        _heads.Clear();
        _conts.Clear();
        _headWord.Clear();
        for (int wi = 0; wi < _words.Length; wi++)
        {
            var w = _words[wi];
            // 書きかけの長さ m(0 = 語彙の頭から)ごとに、残り r の頭になる語に足す
            for (int m = Math.Min(w.Length - 1, d.Length); m >= 0; m--)
            {
                if (!d[(d.Length - m)..].SequenceEqual(w.AsSpan(0, m))) continue;
                var r = w.AsSpan(m);
                bool any = false;
                for (int l = 1; l <= r.Length; l++)
                {
                    if (!CharBoundary(w, m + l) || !_byText!.TryGetValue(Bytes.GetString(r[..l]), out var id) || id >= nv) continue;
                    any = true;
                    Mark(id, m, wi);
                }
                // 文字の切れ目で終わる語が無い(1 文字が複数の語に分かれる)ときだけ、残りの頭に最も長く合う語に足す
                if (!any)
                    for (int l = r.Length; l >= 1; l--)
                        if (_byText!.TryGetValue(Bytes.GetString(r[..l]), out var id) && id < nv) { Mark(id, m, wi); break; }
            }
        }
        if (_boosted.Count == 0) return;
        if (On)
        {
            foreach (var id in _boosted) logits[id] += Boost;
        }
        else
        {
            var key = (_window, n);
            bool after = _force.Count > 0 && key.CompareTo(_lastForce) > 0;
            if (_force.Contains(key))
            {
                // この手で語彙の頭(または続き)の最も確からしい語を選ばせる
                int pick = -1;
                foreach (var id in _boosted) if (pick < 0 || logits[id] > logits[pick]) pick = id;
                logits[pick] = max + 1;
            }
            else if (_force.Count > 0 && key.CompareTo(_force.Min()) > 0 && _boosted.Overlaps(_conts))
            {
                foreach (var id in _conts) logits[id] += Boost; // 寄せた語彙を書き切らせる(新しい語彙の頭には足さない)
            }
            else if (_force.Count == 0 || after)
            {
                int top = -1;
                foreach (var id in _heads) if (top < 0 || _prevLogp[id] > _prevLogp[top]) top = id;
                double g = top < 0 ? 0 : _prevLogp[best] - _prevLogp[top];
                if (g > 0 && g < Boost) _candidates.Add(new Candidate(_window, n, g, Words[_headWord[top]]));
                return;
            }
            else return;
        }
        int chosen = best;
        foreach (var id in _boosted) if (logits[id] > logits[chosen]) chosen = id;
        if (chosen != best) { Flipped = true; Gap += _prevLogp[best] - _prevLogp[chosen]; }
    }

    private readonly HashSet<int> _heads = new();
    private readonly HashSet<int> _conts = new();

    private readonly Dictionary<int, int> _headWord = new();
    private readonly Dictionary<int, int[]> _sequence = new(); // 窓ごとに、ここまでに選んだ語(読みで確かめるときの続きの文字列 <see cref="Continuation"/>)

    private void Mark(int id, int m, int word)
    {
        _boosted.Add(id);
        if (m == 0) { _heads.Add(id); _headWord.TryAdd(id, word); }
        else _conts.Add(id);
    }

    /// <summary>寄せない復号で、手 <paramref name="c"/> から後ろに選んだ語の文字列(その手で語彙を選ばせたら置き換わる所)。</summary>
    public string Continuation(Candidate c)
    {
        if (_vocab is null || !_sequence.TryGetValue(c.Window, out var seq)) return "";
        var bytes = new List<byte>();
        for (int i = c.Step; i < seq.Length; i++) if (seq[i] >= 0 && seq[i] < _eot && seq[i] < _vocab.Length) bytes.AddRange(_vocab[seq[i]]);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// 手 <paramref name="c"/> で語彙を選ばせる見込みがあるか(復号し直す前に読みで確かめる): 語彙の読みが、寄せない復号のその手から後ろの読みの頭と同じ
    /// (<see cref="SoundsAlike"/> と同じ許し)。読みが分からなければ真(復号し直して確かめる)。
    /// </summary>
    public bool Plausible(Candidate c)
    {
        if (Reading is null) return true;
        string? kw, rest;
        try { kw = Reading(c.Word); rest = Reading(Continuation(c)); }
        catch (Exception) { return true; }
        if (kw is null || rest is null) return true;
        var a = Kana(kw);
        var b = Kana(rest);
        if (a.Length == 0) return true;
        var head = b[..Math.Min(b.Length, a.Length)];
        return Distance(a, head) <= Math.Max(1, a.Length / 4);
    }

    private static bool CharBoundary(byte[] w, int i) => i >= w.Length || (w[i] & 0xC0) != 0x80;

    /// <summary>語のバイト列が文字(かな・漢字・英数。文字の途中のバイトも含む)で始まるか。句読点・空白・記号なら偽。</summary>
    public static bool StartsLetter(byte[] t)
    {
        if (t.Length == 0) return false;
        byte b = t[0];
        if (b < 0x80) return char.IsAsciiLetterOrDigit((char)b);
        if (b < 0xC0) return true; // 文字の途中のバイト(前の語の文字の続き)
        try
        {
            var s = new UTF8Encoding(false, true).GetString(t);
            return char.IsLetterOrDigit(s, 0) || s[0] == 'ー';
        }
        catch (DecoderFallbackException) { }
        // 文字の頭のバイトだけ: U+3000〜303F(、。「」など)は E3 80 で始まる。ほかの E3〜E9 はかな・漢字
        if (b == 0xE3) return t.Length < 2 || t[1] != 0x80;
        return b is >= 0xE4 and <= 0xE9;
    }

    /// <summary>
    /// 寄せた復号 <paramref name="biased"/> を採るか(寄せない復号は <paramref name="plain"/>)。
    /// どちらも 1 本の列として数えられ、変わった所がすべて語彙の表記の丸ごとの中にあり(<see cref="Covered"/>)、
    /// 寄せた分を除いた確からしさ(寄せた復号の点 − 寄せない復号の点 + 寄せた分)が −<see cref="Slack"/> 以上のとき。
    /// <paramref name="reading"/>(文の読み)があれば、加えて両方の読みが同じ(<see cref="SoundsAlike"/>)とき。
    /// </summary>
    public static bool Accept(string plain, string biased, DecodeScore plainScore, DecodeScore biasedScore, IReadOnlyList<string> words,
        Func<string, string?>? reading = null) =>
        !plainScore.Unreliable && !biasedScore.Unreliable && plain != biased && Covered(plain, biased, words)
        && biasedScore.Score - plainScore.Score + biasedScore.Gap >= -Slack
        && (reading is null || SoundsAlike(plain, biased, reading));

    /// <summary>
    /// 寄せない文 <paramref name="plain"/> と寄せた文 <paramref name="biased"/> の読みが同じか(同音の書き分けだけか)。読みはかな(カタカナはひらがなに揃え、
    /// 句読点・空白・記号は除く)。同じ頭と同じ終わりを除いた間の違い(編集距離)が、長い方の 4 文字に 1 つ(少なくとも 1)までなら同じとみる
    /// (辞書に無い表記は IME が読みを外す: 「即達」→「そくたっ」)。読みが分からなければ偽(確かめられないものは採らない)。
    /// </summary>
    public static bool SoundsAlike(string plain, string biased, Func<string, string?> reading)
    {
        string? ra, rb;
        try { ra = reading(plain); rb = reading(biased); }
        catch (Exception) { return false; }
        if (ra is null || rb is null) return false;
        var a = Kana(ra);
        var b = Kana(rb);
        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
        var x = a[head..^tail];
        var y = b[head..^tail];
        return Distance(x, y) <= Math.Max(1, Math.Max(x.Length, y.Length) / 4);
    }

    /// <summary>読みを比べる形に揃える: カタカナをひらがなに、句読点・空白・記号を除く。</summary>
    public static string Kana(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= 'ァ' and <= 'ヶ') sb.Append((char)(c - 0x60));
            else if (char.IsLetterOrDigit(c) || c == 'ー') sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Distance(string x, string y)
    {
        var d = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) d[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                int cur = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (x[i - 1] == y[j - 1] ? 0 : 1));
                prev = cur;
            }
        }
        return d[y.Length];
    }

    /// <summary>
    /// <paramref name="biased"/> が <paramref name="plain"/> と違う所(足した・置き換えた文字、消した文字の位置)が、すべて <paramref name="biased"/> の中の
    /// 語彙の表記の丸ごとの出現に入るか(語彙の頭の 1 文字だけが入った「河野」→「河」のような置き換えは入らない)。
    /// </summary>
    public static bool Covered(string plain, string biased, IReadOnlyList<string> words)
    {
        int n = plain.Length, m = biased.Length;
        var charIn = new bool[m];     // biased の文字 j が語彙の出現の中か
        var gapIn = new bool[m + 1];  // biased の文字 j の前の切れ目が語彙の出現の中(頭と終わりの切れ目を含む)か
        foreach (var w in words.Where(w => w.Length > 0))
            for (int at = biased.IndexOf(w, StringComparison.Ordinal); at >= 0; at = biased.IndexOf(w, at + 1, StringComparison.Ordinal))
            {
                for (int k = at; k < at + w.Length; k++) charIn[k] = true;
                for (int k = at; k <= at + w.Length; k++) gapIn[k] = true;
            }
        // 同じ頭と同じ終わりは除いて、間だけを最長共通部分列で突き合わせる(長い発話でも表を小さく)
        int head = 0;
        while (head < n && head < m && plain[head] == biased[head]) head++;
        int tail = 0;
        while (tail < n - head && tail < m - head && plain[n - 1 - tail] == biased[m - 1 - tail]) tail++;
        int pn = n - head - tail, bn = m - head - tail;
        var lcs = new int[pn + 1, bn + 1];
        for (int i = pn - 1; i >= 0; i--)
            for (int j = bn - 1; j >= 0; j--)
                lcs[i, j] = plain[head + i] == biased[head + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        for (int i = 0, j = 0; i < pn || j < bn;)
        {
            if (i < pn && j < bn && plain[head + i] == biased[head + j]) { i++; j++; }
            else if (j < bn && (i == pn || lcs[i, j + 1] >= lcs[i + 1, j])) { if (!charIn[head + j]) return false; j++; } // biased に足した・置き換えた文字
            else { if (!gapIn[head + j]) return false; i++; } // plain の文字が消えた(消えた位置は biased の文字 head + j の前)
        }
        return true;
    }
}

/// <summary>語彙の頭を選ばせられる手(<see cref="KeywordBias.Candidates"/>)。窓の番号と、窓の中で何手目か、最良の語との対数確率の差、その頭の語彙。</summary>
public readonly record struct Candidate(int Window, int Step, double Gap, string Word);

/// <summary>1 回の復号の点(<see cref="KeywordBias.Result"/>)。</summary>
public readonly record struct DecodeScore(bool Flipped, bool Unreliable, double Score, double Gap);
