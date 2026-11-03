using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class SentenceEndTests
{
    // Haas-ja-v1.0(speech.cpp 0.10.1)の実測: 文中の句点は書くが、発話の終わりの句点はほとんど書かない
    [Theory]
    [InlineData("明日の打ち合わせは会議室でやります", "明日の打ち合わせは会議室でやります。")]
    [InlineData("この資料はいつまでに提出すればいいですか", "この資料はいつまでに提出すればいいですか。")]
    [InlineData("画面の配置について意見をください", "画面の配置について意見をください。")]
    [InlineData("今回のスプリントでは検索機能の改善に取り組みます。以上です", "今回のスプリントでは検索機能の改善に取り組みます。以上です。")]
    [InlineData("なんて綺麗な夕焼けなのでしょう。ああ、ずっと見ていたいです", "なんて綺麗な夕焼けなのでしょう。ああ、ずっと見ていたいです。")]
    [InlineData("以上", "以上。")]
    [InlineData("了解しました", "了解しました。")]
    [InlineData("行きましょう", "行きましょう。")]
    [InlineData("そうですね", "そうですね。")]
    [InlineData("こんにちは", "こんにちは。")]
    [InlineData("テスト", "テスト。")]
    public void 言い切った発話は句点で終える(string text, string expected) => Assert.Equal(expected, SentenceEnd.Close(text));

    // whisper は途中で止めた発話を「…件なんですが、」と読点で終える。同じ付き方にする(句点を付けない)
    [Theory]
    [InlineData("来週の打ち合わせの件なんですが", "来週の打ち合わせの件なんですが、")]
    [InlineData("確認したんですけど", "確認したんですけど、")]
    [InlineData("時間がないので", "時間がないので、")]
    [InlineData("資料を見て", "資料を見て、")]
    [InlineData("会議室で", "会議室で、")]
    [InlineData("その件については", "その件については、")]
    [InlineData("来週の打ち合わせの", "来週の打ち合わせの、")]
    [InlineData("資料を", "資料を、")]
    [InlineData("明日まで", "明日まで、")]
    public void 途中で止めた発話は読点で終える(string text, string expected) => Assert.Equal(expected, SentenceEnd.Close(text));

    // 既に区切りがある・英数字で終わる・空は変えない
    [Theory]
    [InlineData("明日の打ち合わせは会議室でやります。")]
    [InlineData("この資料はいつまでに提出すればいいですか？")]
    [InlineData("いいですか?")]
    [InlineData("来週の打ち合わせの件なんですが、")]
    [InlineData("ちょっと待って…")]
    [InlineData("「了解」")]
    [InlineData("部長が会議の最後に、Let's meet again next week")]
    [InlineData("ファイル名は report.pdf")]
    [InlineData("ここのつ、10")]
    [InlineData("ＡＢＣ")]
    [InlineData("")]
    public void 区切りや英数字で終わる発話は変えない(string text) => Assert.Same(text, SentenceEnd.Close(text));

    [Fact]
    public void 末尾の空白は除いてから付ける() => Assert.Equal("やります。", SentenceEnd.Close("やります "));

    [Fact]
    public void 台本の期待_endsWith_は届いた文字列の終わりを比べる()
    {
        var e = new Voice2Txt.Core.Verification.Expectation
        {
            Deliveries = [new() { Text = "来週の打ち合わせの件なんですが、", MinSimilarity = 0.8, EndsWith = "、" }],
        };
        Voice2Txt.Core.Verification.VerifyResult R(string t) => new() { Completed = true, Deliveries = [new() { Seq = 1, To = "textbox", Text = t }] };
        Assert.Empty(Voice2Txt.Core.Verification.Evaluator.Check(e, R("来週の打ち合わせの件なんですが、")));
        Assert.Contains(Voice2Txt.Core.Verification.Evaluator.Check(e, R("来週の打ち合わせの件なんですが")), f => f.Contains("終わりが「、」でない"));
        Assert.Contains(Voice2Txt.Core.Verification.Evaluator.Check(e, R("来週の打ち合わせの件なんですが。")), f => f.Contains("終わりが「、」でない"));
    }

    [Fact]
    public void 常駐時と検証モードは区切りを付ける() => Assert.True(PttOptions.From(new AppSettings()).CloseSentence);

    // 受け入れ: 平叙文・質問文・途中で止めた発話を 2 回続けて話すと、貼られた文章の繋ぎ目が whisper と同じ付き方で読める
    [Fact]
    public async Task 続けて話した発話の繋ぎ目が確定版だけで分かる()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, CloseSentence: true));
        var said = new Queue<string>(["明日の打ち合わせは会議室でやります", "この資料はいつまでに提出すればいいですか", "来週の打ち合わせの件なんですが"]);
        r.Engine.Text = _ => { var t = said.Dequeue(); said.Enqueue(t); return t; };
        for (int i = 0; i < 6; i++) { r.Utter(1000); await r.Idle(); }
        Assert.Equal(
            "明日の打ち合わせは会議室でやります。この資料はいつまでに提出すればいいですか。来週の打ち合わせの件なんですが、"
            + "明日の打ち合わせは会議室でやります。この資料はいつまでに提出すればいいですか。来週の打ち合わせの件なんですが、",
            string.Concat(r.Paster.Pasted.Select(p => p.Text)));
    }

    [Fact]
    public async Task 言い淀みを取り除いた跡の読点は付け直す()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, CloseSentence: true));
        r.Engine.Text = _ => "来週の件なんですが、えっと";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("来週の件なんですが、", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 言い淀みだけの発話は区切りを付けずに取り消す()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, CloseSentence: true));
        r.Engine.Text = _ => "えっと";
        r.Utter(1000);
        await r.Idle();
        Assert.Empty(r.Paster.Pasted);
    }

    [Fact]
    public async Task 途中経過には付けない()
    {
        await using var r = new Rig(new PttOptions(InterimIntervalMs: 10, CloseSentence: true));
        r.Recorder.NextAudio = FakeRecorder.Tone(1.0);
        r.Recorder.SnapshotAudio = FakeRecorder.Tone(0.6);
        r.Engine.Text = _ => "明日やります";
        r.Ptt.OnTalkKeyDown();
        for (int i = 0; i < 300 && r.Ptt.CurrentView.Interim is null; i++) await Task.Delay(10);
        Assert.Equal("明日やります", r.Ptt.CurrentView.Interim);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("明日やります。", Assert.Single(r.Paster.Pasted).Text);
    }
}
