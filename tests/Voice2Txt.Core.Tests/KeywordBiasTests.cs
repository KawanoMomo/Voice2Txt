using System.Text;
using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

// カスタム語彙を whisper の復号に効かせる(語彙の表記へ寄せる)。初期プロンプトに表記を足すだけでは、
// 動詞の活用形(「銭湯に酔って」→「寄って」)は変わらなかった(BLK-transcriber-20261104-1100 の差し戻し)
public class KeywordBiasTests
{
    // 偽の語の一覧(whisper の語の番号 → バイト列)。eot より後ろは時刻などの特別な語
    private static readonly byte[] KiLead = Encoding.UTF8.GetBytes("寄")[..2]; // 「寄」の頭の 2 バイト(同じ頭の漢字に共通)
    private static readonly string[] Texts = ["銭湯", "に", "酔", "寄", "って", "、", "速", "達", "即", "河", "川", "野", "へ"];
    private const int Sento = 0, Ni = 1, You = 2, Yo = 3, Tte = 4, Comma = 5, Soku = 6, Tatsu = 7, Soku2 = 8, Ka = 9, Kawa = 10, No = 11, He = 12, Lead = 13;
    private const int Eot = 14, Timestamp = 15;

    private static KeywordBias Make(params string[] keywords)
    {
        var b = new KeywordBias(keywords);
        var vocab = Texts.Select(Encoding.UTF8.GetBytes).Append(KiLead).Append(Encoding.UTF8.GetBytes("[_EOT_]")).Append(Encoding.UTF8.GetBytes("[_TT_1]")).ToList();
        b.SetVocabulary(vocab, Eot);
        return b;
    }

    private static float[] Logits(params (int Id, float Value)[] set)
    {
        var l = Enumerable.Repeat(-10f, Timestamp + 1).ToArray();
        foreach (var (id, v) in set) l[id] = v;
        return l;
    }

    [Fact]
    public void 語彙が空なら寄せない()
    {
        var b = Make();
        Assert.True(b.IsEmpty);
        b.Begin(on: true);
        Assert.False(b.On);
        var l = Logits((You, 5), (Yo, 3));
        b.OnLogits([Sento, Ni], l);
        Assert.Equal(3, l[Yo]);
        Assert.False(b.Flipped);
    }

    [Fact]
    public void 語彙の頭になる語に点を足し_同音の別の語より上になれば選ぶ語が変わったと覚える()
    {
        var b = Make("寄って");
        b.Begin(on: true);
        var l = Logits((You, 5), (Yo, 3)); // 寄せなければ「酔」
        b.OnLogits([Sento, Ni], l);
        Assert.Equal(3 + KeywordBias.Bonus, l[Yo]);
        Assert.Equal(5, l[You]);
        Assert.True(b.Flipped);
        Assert.Equal(2, b.Gap, 3); // 寄せる前の対数確率の差 = 点の差
    }

    [Fact]
    public void 書きかけの語彙があればその続きの語に足す()
    {
        var b = Make("寄って");
        b.Begin(on: true);
        var l = Logits((Tte, 1), (He, 2));
        b.OnLogits([Sento, Ni, Yo], l);
        Assert.Equal(1 + KeywordBias.Bonus, l[Tte]);
        Assert.Equal(2, l[He]);
    }

    [Fact]
    public void 句読点や終わりを書こうとしているときは足さない()
    {
        // 「届くように、速達で」の読点の位置で「速」に足すと読点が落ちた。句読点・区切り・終わりの位置は動かさない
        var b = Make("速達");
        b.Begin(on: true);
        var l = Logits((Comma, 5), (Soku, 4));
        b.OnLogits([Ni], l);
        Assert.Equal(4, l[Soku]);
        var e = Logits((Eot, 5), (Soku, 4));
        b.OnLogits([Ni], e);
        Assert.Equal(4, e[Soku]);
        var t = Logits((Timestamp, 5), (Soku, 4));
        b.OnLogits([Ni], t);
        Assert.Equal(4, t[Soku]);
        Assert.False(b.Flipped);
    }

    [Fact]
    public void 文字の頭のバイトだけの語には_文字の切れ目で終わる語があれば足さない()
    {
        // 「寄」の頭の 2 バイトに足すと、同じ頭の別の漢字(「寒」など)まで寄った
        var b = Make("寄って");
        b.Begin(on: true);
        var l = Logits((You, 5), (Yo, 3), (Lead, 1));
        b.OnLogits([Ni], l);
        Assert.Equal(1, l[Lead]);
        Assert.Equal(3 + KeywordBias.Bonus, l[Yo]);
    }

    [Fact]
    public void 寄せないときも選んだ語の点を数える()
    {
        var b = Make("寄って");
        b.Begin(on: false);
        var l1 = Logits((Sento, 0));
        b.OnLogits([], l1);
        var l2 = Logits((Ni, 0));
        b.OnLogits([Sento], l2);
        Assert.Equal(l1[Yo], Logits()[Yo]); // 点は変えない
        Assert.True(b.Score < 0 && b.Score > -0.01); // 「銭湯」はほぼ確実(他は -10)
        Assert.False(b.Unreliable);
        Assert.False(b.Flipped);
    }

    [Fact]
    public void 別の列が混ざると点を数えられないと覚える()
    {
        // 温度を上げたやり直しでは複数の列を並べて復号する(同じ手の長さで続けて呼ばれる)
        var b = Make("寄って");
        b.Begin(on: false);
        b.OnLogits([Sento], Logits((Ni, 0)));
        b.OnLogits([Sento], Logits((Ni, 0)));
        Assert.True(b.Unreliable);
        b.Begin(on: false);
        Assert.False(b.Unreliable);
    }

    [Theory]
    [InlineData("寄", true)]
    [InlineData("って", true)]
    [InlineData("a", true)]
    [InlineData("、", false)]
    [InlineData("。", false)]
    [InlineData(" ", false)]
    [InlineData(".", false)]
    public void 文字で始まる語か(string text, bool letter) => Assert.Equal(letter, KeywordBias.StartsLetter(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void 文字の頭のバイトだけの語は_かな漢字なら文字_全角の句読点の頭なら文字でない()
    {
        Assert.True(KeywordBias.StartsLetter(KiLead));
        Assert.False(KeywordBias.StartsLetter(Encoding.UTF8.GetBytes("、")[..2]));
        Assert.True(KeywordBias.StartsLetter([0x84])); // 文字の途中のバイト(前の語の文字の続き)
    }

    [Theory]
    [InlineData("銭湯に酔って、熱い", "銭湯に寄って、熱い", true)]   // 語彙の丸ごとの中の置き換え
    [InlineData("道の端によりました", "道の端に寄りました", false)] // 「寄って」の頭の 1 文字だけ
    [InlineData("近所によって", "近所に寄って", true)]
    [InlineData("近所の銭湯", "近所の寄って銭湯", true)]           // 語彙を丸ごと足した
    [InlineData("仕事、", "寄って仕事、寄って", true)]
    [InlineData("仕事、", "寄って仕事、寄ってき", false)]      // 語彙の後ろの「き」は語彙の外
    [InlineData("銭湯に酔って", "銭湯に酔って", true)]             // 違いが無い
    public void 変わった所がすべて語彙の表記の中にあるか(string plain, string biased, bool covered) =>
        Assert.Equal(covered, KeywordBias.Covered(plain, biased, ["寄って", "速達"]));

    [Theory]
    [InlineData("定例会議を行います", "定例議を行います", "議事録")] // 語彙の頭の 1 文字に置き換わり、前の字が消えた
    [InlineData("川へ行って", "河へ行って", "河野")]               // 語彙の頭の 1 文字だけ
    public void 語彙の頭の1文字だけが入った置き換えは語彙の中に入らない(string plain, string biased, string word) =>
        Assert.False(KeywordBias.Covered(plain, biased, [word]));

    [Fact]
    public void 消えた文字は語彙の出現の端なら中に入る()
    {
        Assert.True(KeywordBias.Covered("速 達で", "速達で", ["速達"]));
        Assert.False(KeywordBias.Covered("届くように、速達で", "届くように速達で", ["寄って"]));
    }

    [Fact]
    public void 寄せた方を採るのは_寄せた分を除いた確からしさが寄せない復号より下がらないとき()
    {
        // 「銭湯に寄って」: 寄せた分 2.18、点の差 -2.04(残りの確からしさはむしろ上がる)→ 採る
        Assert.True(KeywordBias.Accept("銭湯に酔って", "銭湯に寄って", new(false, false, -2.51, 0), new(true, false, -4.55, 2.18), ["寄って"]));
        // 語彙の名前「河野」で契約書の「甲と乙」が「河野と河野」: 寄せた分 0.89、点の差 -2.09(後ろの語が不自然になる)→ 採らない
        Assert.False(KeywordBias.Accept("甲とオツの間", "河野と河野の間", new(false, false, -3.83, 0), new(true, false, -5.92, 0.89), ["河野"]));
        // 点が数えられない復号は採らない
        Assert.False(KeywordBias.Accept("銭湯に酔って", "銭湯に寄って", new(false, false, -2.51, 0), new(true, true, -4.55, 2.18), ["寄って"]));
        // 語彙の外が変わったものは点にかかわらず採らない
        Assert.False(KeywordBias.Accept("川へ", "河へ", new(false, false, -1.42, 0), new(true, false, -3.29, 1.86), ["河野"]));
    }

    // ---- 読みで確かめる当て方(「週末に来るまで」→「車で」。既定の Whisper は「来」を「車」より 6〜7 だけ上に置き、点 3 では寄らない)

    private static readonly string[] KTexts = ["週末", "に", "来", "る", "まで", "車", "で", "紅葉", "秋", "[_EOT_]"];
    private const int KShu = 0, KNi = 1, KKu = 2, KRu = 3, KMade = 4, KKuruma = 5, KDe = 6, KKouyou = 7, KAki = 8, KEot = 9;

    // 偽の IME: 表に無い語は 1 文字ずつ表から読む
    private static readonly Dictionary<string, string> Yomi = new()
    {
        ["週末"] = "しゅうまつ", ["に"] = "に", ["来"] = "く", ["る"] = "る", ["まで"] = "まで", ["車"] = "くるま", ["で"] = "で", ["紅葉"] = "こうよう", ["秋"] = "あき",
        ["甲"] = "こう", ["と"] = "と", ["乙"] = "おつ", ["河野"] = "こうの", ["即達"] = "そくたっ", ["速達"] = "そくたつ", ["、"] = "、", ["銭湯"] = "せんとう", ["酔"] = "よ", ["寄"] = "よ", ["って"] = "って",
    };

    private static string? FakeReading(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length;)
        {
            var hit = Yomi.Keys.Where(k => string.CompareOrdinal(s, i, k, 0, k.Length) == 0).OrderByDescending(k => k.Length).FirstOrDefault();
            if (hit is null) return null;
            sb.Append(Yomi[hit]);
            i += hit.Length;
        }
        return sb.ToString();
    }

    private static KeywordBias MakeK(Func<string, string?>? reading, params string[] keywords)
    {
        var b = new KeywordBias(keywords, reading);
        b.SetVocabulary(KTexts.Select(Encoding.UTF8.GetBytes).ToList(), KEot);
        return b;
    }

    private static float[] KLogits(params (int Id, float Value)[] set)
    {
        var l = Enumerable.Repeat(-20f, KTexts.Length).ToArray();
        foreach (var (id, v) in set) l[id] = v;
        return l;
    }

    [Fact]
    public void 読みが分からなければ点は小さいまま_分かれば大きい点で寄せる()
    {
        Assert.Equal(KeywordBias.Bonus, MakeK(null, "車").Boost);
        Assert.Equal(KeywordBias.ReadingBonus, MakeK(FakeReading, "車").Boost);
        Assert.True(KeywordBias.ReadingBonus > KeywordBias.Bonus);
    }

    [Fact]
    public void 読みで確かめるときは_寄せずに復号して語彙の頭を選ばせられる手を集める()
    {
        var b = MakeK(FakeReading, "車");
        b.Begin(on: false);
        b.OnLogits([], KLogits((KShu, 0), (KKuruma, -9)));      // 差 9 は点より大きい: 集めない
        b.OnLogits([KShu], KLogits((KNi, 0)));
        var l = KLogits((KKu, 0), (KKuruma, -6.5f));               // 「来」が「車」より 6.5 上
        b.OnLogits([KShu, KNi], l);
        Assert.Equal(-6.5f, l[KKuruma]);                           // 寄せない復号では点を変えない
        b.OnLogits([KShu, KNi, KKu], KLogits((KRu, 0)));
        b.OnLogits([KShu, KNi, KKu, KRu], KLogits((KMade, 0)));
        b.OnLogits([KShu, KNi, KKu, KRu, KMade], KLogits((KEot, 0)));
        var c = Assert.Single(b.Candidates);
        Assert.Equal((0, 2, "車"), (c.Window, c.Step, c.Word));
        Assert.Equal(6.5, c.Gap, 2);
        Assert.Equal("来るまで", b.Continuation(c));
        Assert.False(b.Flipped);
    }

    [Fact]
    public void 集めた手でだけ語彙の頭を選ばせ_その後は書きかけの語彙の続きにだけ足す()
    {
        var b = MakeK(FakeReading, "車で");
        b.Begin(on: false);
        b.OnLogits([], KLogits((KShu, 0)));
        b.OnLogits([KShu], KLogits((KNi, 0)));
        b.OnLogits([KShu, KNi], KLogits((KKu, 0), (KKuruma, -6.5f)));
        var c = Assert.Single(b.Candidates);

        b.BeginForced([c]);
        var l0 = KLogits((KShu, 0), (KKuruma, -3));
        b.OnLogits([], l0);
        Assert.Equal(-3, l0[KKuruma]);                              // 選ばせる手より前は変えない
        b.OnLogits([KShu], KLogits((KNi, 0)));
        var l = KLogits((KKu, 0), (KKuruma, -6.5f));
        b.OnLogits([KShu, KNi], l);
        Assert.True(l[KKuruma] > l[KKu]);                          // この手で「車」を選ばせる
        Assert.True(b.Flipped);
        Assert.Equal(6.5, b.Gap, 2);
        var l2 = KLogits((KMade, 0), (KDe, -2));
        b.OnLogits([KShu, KNi, KKuruma], l2);
        Assert.Equal(-2 + KeywordBias.ReadingBonus, l2[KDe]);      // 語彙「車で」を書き切らせる
        var l3 = KLogits((KKouyou, 0), (KKuruma, -1));
        b.OnLogits([KShu, KNi, KKuruma, KDe], l3);
        Assert.Equal(-1, l3[KKuruma]);                              // 新しい語彙の頭には足さない(後ろの手として集める)
        Assert.Equal(4, Assert.Single(b.Candidates).Step);
    }

    [Theory]
    [InlineData("週末に来るまで紅葉", "週末に車で紅葉", true)]   // 同音の書き分け
    [InlineData("銭湯に酔って", "銭湯に寄って", true)]
    [InlineData("即達で", "速達で", true)]                       // 辞書に無い表記は IME が読みを外す(そくたっ)。1 文字の違いは許す
    [InlineData("甲と乙", "河野と河野", false)]                 // 音の違う語に寄った
    [InlineData("秋に", "車に", false)]
    public void 寄せた文と寄せない文の読みが同じか(string plain, string biased, bool alike) =>
        Assert.Equal(alike, KeywordBias.SoundsAlike(plain, biased, FakeReading));

    [Fact]
    public void 読みが分からない文は同じとみない()
    {
        Assert.False(KeywordBias.SoundsAlike("週末に来るまで", "週末に車で", _ => null));
        Assert.False(KeywordBias.SoundsAlike("週末に来るまで", "週末に車で", _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void 読みを比べる形はひらがなで_句読点と空白を除く() =>
        Assert.Equal("くるまでこうよう１０", KeywordBias.Kana("クルマで、 こうよう。１０"));

    [Fact]
    public void 読みで確かめるときは_読みが違えば語彙の中だけの変化でも採らない()
    {
        var plainScore = new DecodeScore(false, false, -3.83, 0);
        var biasedScore = new DecodeScore(true, false, -3.9, 0.89);
        Assert.True(KeywordBias.Accept("甲と乙", "河野と河野", plainScore, biasedScore, ["河野"]));               // 読みで確かめなければ点だけで採る
        Assert.False(KeywordBias.Accept("甲と乙", "河野と河野", plainScore, biasedScore, ["河野"], FakeReading)); // 読みが違う
        Assert.True(KeywordBias.Accept("週末に来るまで紅葉", "週末に車で紅葉", plainScore, biasedScore, ["車"], FakeReading));
    }

    [Fact]
    public void 選ばせる前に_語彙の読みがその手から後ろの読みの頭と合うかを見る()
    {
        var b = MakeK(FakeReading, "車");
        b.Begin(on: false);
        b.OnLogits([], KLogits((KAki, 0), (KKuruma, -5)));           // 文頭「秋」の手にも「車」が 5 差で来る
        b.OnLogits([KAki], KLogits((KNi, 0)));
        b.OnLogits([KAki, KNi], KLogits((KKu, 0), (KKuruma, -6.5f)));
        b.OnLogits([KAki, KNi, KKu], KLogits((KRu, 0)));
        b.OnLogits([KAki, KNi, KKu, KRu], KLogits((KMade, 0)));
        b.OnLogits([KAki, KNi, KKu, KRu, KMade], KLogits((KEot, 0)));
        var cs = b.Candidates;
        Assert.Equal(2, cs.Count);
        Assert.Equal(0, cs[0].Step);                                  // 差の小さい順
        Assert.False(b.Plausible(cs[0]));                             // 「くるま」と「あきにくるまで」の頭は合わない(復号し直さない)
        Assert.True(b.Plausible(cs[1]));                              // 「くるま」と「くるまで」
        Assert.True(MakeK(null, "車").Plausible(cs[0]));              // 読みが分からなければ確かめない
    }

    [Fact]
    public void 語彙の表記は初期プロンプトと同じ_読みは除き読めない語は除く()
    {
        Assert.Equal(["寄って", "Haas"], new KeywordBias(["寄って", "Haas=ハース", "bad=", "寄って"]).Words);
        Assert.Equal(["寄って", "Haas"], Decoding.KeywordWords(["寄って", "Haas=ハース", "bad=", "寄って"]));
    }
}
