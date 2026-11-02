using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class SpacingTests
{
    // primary の m02・m03・m06・m08 の形(文 + 「以上です。」)。whisper が句点の後に半角の空白を書いた
    [Theory]
    [InlineData("今回のスプリントでは検索機能の改善に取り組みます。 以上です。", "今回のスプリントでは検索機能の改善に取り組みます。以上です。")]
    [InlineData("レビューのコメントに対応したので、もう一度見てください。 以上です。", "レビューのコメントに対応したので、もう一度見てください。以上です。")]
    [InlineData("ユーザーからの問い合わせに返信しておきます。 以上です。", "ユーザーからの問い合わせに返信しておきます。以上です。")]
    [InlineData("依存ライブラリの更新は来週にまとめて行います。 以上です。", "依存ライブラリの更新は来週にまとめて行います。以上です。")]
    [InlineData("一つ目です。  二つ目です。　三つ目です。", "一つ目です。二つ目です。三つ目です。")]
    [InlineData("確認します、 よろしく", "確認します、よろしく")]
    [InlineData("会議 室", "会議室")]
    [InlineData("1つ、 2つ、 10。", "1つ、2つ、10。")]
    [InlineData("ＡＢＣ です", "ＡＢＣです")]
    public void 日本語の文字の間の空白を詰める(string text, string expected) => Assert.Equal(expected, Spacing.Tidy(text));

    // 英文の語の間、英文と日本語の境は今のまま
    [Theory]
    [InlineData("来週の出張について、Please confirm the hotel booking.と英語で書かれたメールが届いたので、返信をお願いします。")]
    [InlineData("部長が会議の最後に、Let's meet again next week.と言っていました。")]
    [InlineData("来週の出張について、 Please confirm the hotel booking. と書かれていました。")]
    [InlineData("確認しました。 Thank you.")]
    [InlineData("Version 2 です")]
    [InlineData("ファイル名は、report_最終.pdfです。")]
    [InlineData("一期一会の精神で、臨機応変に対応し、一生懸命に取り組みます。")]
    [InlineData("")]
    public void 英文の空白と英文と日本語の境は変えない(string text) => Assert.Same(text, Spacing.Tidy(text));

    [Fact]
    public void 先頭と末尾の空白はそのまま() => Assert.Equal(" です。以上 ", Spacing.Tidy(" です。 以上 "));

    [Fact]
    public async Task 確定版は日本語の文の間に空白を入れずに届く()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords));
        r.Engine.Text = _ => "今回のスプリントでは検索機能の改善に取り組みます。 以上です。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("今回のスプリントでは検索機能の改善に取り組みます。以上です。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 言い淀みを取り除いた跡の空白も詰める()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords));
        r.Engine.Text = _ => "明日やります。 えっと 以上です。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("明日やります。以上です。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 言い淀みを取り除かない設定でも空白は詰める()
    {
        await using var r = new Rig(new PttOptions(Fillers: null));
        r.Engine.Text = _ => "明日やります。 以上です。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("明日やります。以上です。", Assert.Single(r.Paster.Pasted).Text);
    }
}
