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

    [Fact]
    public void 語彙の表記は初期プロンプトと同じ_読みは除き読めない語は除く()
    {
        Assert.Equal(["寄って", "Haas"], new KeywordBias(["寄って", "Haas=ハース", "bad=", "寄って"]).Words);
        Assert.Equal(["寄って", "Haas"], Decoding.KeywordWords(["寄って", "Haas=ハース", "bad=", "寄って"]));
    }
}
