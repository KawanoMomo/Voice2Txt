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

    public KeywordBias(IEnumerable<string>? keywords)
    {
        Words = Decoding.KeywordWords(keywords);
        _words = Words.Select(Encoding.UTF8.GetBytes).ToArray();
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

    /// <summary>次の復号を始める。<paramref name="on"/> が偽なら寄せずに点だけ数える。</summary>
    public void Begin(bool on)
    {
        On = on && !IsEmpty;
        Flipped = Unreliable = false;
        Score = Gap = 0;
        _prevN = -1;
    }

    /// <summary>この復号の結果(<see cref="Accept"/> に渡す)。</summary>
    public DecodeScore Result => new(Flipped, Unreliable, Score, Gap);

    /// <summary>
    /// 復号の 1 手(whisper.cpp の logits filter)。<paramref name="tokens"/> はこの列でここまでに選んだ語、<paramref name="logits"/> は次の語の点(寄せるならここに足す)。
    /// </summary>
    public void OnLogits(ReadOnlySpan<int> tokens, Span<float> logits)
    {
        if (_vocab is null) return;
        int n = tokens.Length, nv = Math.Min(logits.Length, _vocab.Length);
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
        if (!On || best >= _eot || !StartsLetter(_vocab[best])) return;

        _decoded.Clear();
        foreach (var t in tokens) if (t >= 0 && t < _eot && t < _vocab.Length) _decoded.AddRange(_vocab[t]);
        var d = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_decoded);
        _boosted.Clear();
        foreach (var w in _words)
        {
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
                    if (_boosted.Add(id)) logits[id] += Bonus;
                }
                // 文字の切れ目で終わる語が無い(1 文字が複数の語に分かれる)ときだけ、残りの頭に最も長く合う語に足す
                if (!any)
                    for (int l = r.Length; l >= 1; l--)
                        if (_byText!.TryGetValue(Bytes.GetString(r[..l]), out var id) && id < nv) { if (_boosted.Add(id)) logits[id] += Bonus; break; }
            }
        }
        if (_boosted.Count == 0) return;
        int after = best;
        foreach (var id in _boosted) if (logits[id] > logits[after]) after = id;
        if (after != best) { Flipped = true; Gap += _prevLogp[best] - _prevLogp[after]; }
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
    /// </summary>
    public static bool Accept(string plain, string biased, DecodeScore plainScore, DecodeScore biasedScore, IReadOnlyList<string> words) =>
        !plainScore.Unreliable && !biasedScore.Unreliable && plain != biased && Covered(plain, biased, words)
        && biasedScore.Score - plainScore.Score + biasedScore.Gap >= -Slack;

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

/// <summary>1 回の復号の点(<see cref="KeywordBias.Result"/>)。</summary>
public readonly record struct DecodeScore(bool Flipped, bool Unreliable, double Score, double Gap);
