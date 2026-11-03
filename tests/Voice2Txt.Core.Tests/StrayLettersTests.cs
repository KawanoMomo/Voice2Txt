using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class StrayLettersTests
{
    // Haas が言い淀み・息を小文字 1 文字で書いた形(corpus redo-02 の「ええと」→「aと」、実機の所見の「h」「a」)
    [Theory]
    [InlineData("資料は、あの三部aと五部印刷しておいてください。", "資料は、あの三部と五部印刷しておいてください。")]
    [InlineData("明日はh会議室でやります。", "明日は会議室でやります。")]
    [InlineData("三部、a、五部", "三部、五部")]
    [InlineData("三部、 a 、五部", "三部、五部")]
    [InlineData("三部 a 五部", "三部 五部")]
    [InlineData("h、資料は送ります。", "資料は送ります。")]
    [InlineData("a 資料は送ります", "資料は送ります")]
    [InlineData("送ります。a", "送ります。")]
    [InlineData("送ります h", "送ります")]
    [InlineData("a", "")]
    [InlineData("それはhaでした", "それはhaでした")]
    public void 発話に無い小文字1文字の英字を取り除く(string text, string expected) =>
        Assert.Equal(expected, StrayLetters.Remove(text).Text);

    [Fact]
    public void 取り除いた数を返す()
    {
        Assert.Equal(2, StrayLetters.Remove("h資料はa送ります").Removed);
        Assert.Equal(0, StrayLetters.Remove("資料は送ります").Removed);
    }

    // 意味のある英字(大文字 1 文字・英単語・英文・型番・単位・記号でつながった語)は残す
    [Theory]
    [InlineData("A案とB案を比べます。")]
    [InlineData("Cさんに送ります。")]
    [InlineData("ビタミンCを取ります。")]
    [InlineData("来週の出張について、Please confirm the hotel booking.と英語で書かれたメールが届いた")]
    [InlineData("I have a pen.")]
    [InlineData("a few days")]
    [InlineData("サイズは3x4です。")]
    [InlineData("長さは5 mです。")]
    [InlineData("e-mailで送ります。")]
    [InlineData("ファイル名は、report_最終.pdfです。")]
    [InlineData("iPhoneで撮りました")]
    [InlineData("x.comを開く")]
    [InlineData("")]
    public void 意味のある英字は残す(string text) => Assert.Same(text, StrayLetters.Remove(text).Text);

    [Fact]
    public async Task 届ける確定版からはぐれ英字を取り除き_言い淀みも取り除く()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, RemoveStrayLetters: true));
        r.Engine.Text = _ => "h、ええと、a総務に出してください。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("総務に出してください。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 言い淀みを取り除かない設定でもはぐれ英字は取り除く()
    {
        await using var r = new Rig(PttOptions.From(new AppSettings { RemoveFillers = false, Engine = Engines.Haas }));
        r.Engine.Text = _ => "資料は、あの三部aと五部印刷しておいてください。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("資料は、あの三部と五部印刷しておいてください。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public void はぐれ英字を取り除くのはengine_haasのときだけ()
    {
        Assert.True(PttOptions.From(new AppSettings { Engine = Engines.Haas }).RemoveStrayLetters);
        Assert.False(PttOptions.From(new AppSettings()).RemoveStrayLetters);
        Assert.False(PttOptions.From(new AppSettings { Engine = Engines.Whisper }).RemoveStrayLetters);
    }

    // whisper は日本語の中の変数・軸の名前を小文字 1 文字で書く。話した語なので確定版にも途中経過にもそのまま残す
    [Theory]
    [InlineData("x軸とy軸の値を確認してください。", "x軸とy軸の値を確認してください。")]
    [InlineData("変数xに0を代入", "変数xに0を代入。")]
    [InlineData("n個のデータを、k平均法で分類します。", "n個のデータを、k平均法で分類します。")]
    [InlineData("k番目の要素を取り出します。", "k番目の要素を取り出します。")]
    public async Task whisperの確定版の小文字1文字の英字は残す(string said, string expected)
    {
        await using var r = new Rig(PttOptions.From(new AppSettings { Engine = Engines.Whisper }));
        r.Engine.Text = _ => said;
        r.Utter(1000);
        await r.Idle();
        Assert.Equal(expected, Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task whisperの途中経過の小文字1文字の英字は残す()
    {
        await using var r = new Rig(PttOptions.From(new AppSettings { Engine = Engines.Whisper, ShowInterim = true }) with { InterimIntervalMs = 10 });
        r.Recorder.NextAudio = FakeRecorder.Tone(1.0);
        r.Recorder.SnapshotAudio = FakeRecorder.Tone(0.6);
        r.Engine.Text = _ => "x軸とy軸の値";
        r.Ptt.OnTalkKeyDown();
        for (int i = 0; i < 300 && r.Ptt.CurrentView.Interim is null; i++) await Task.Delay(10);
        Assert.Equal("x軸とy軸の値", r.Ptt.CurrentView.Interim);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("x軸とy軸の値。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task haasの途中経過からははぐれ英字を取り除く()
    {
        await using var r = new Rig(new PttOptions(InterimIntervalMs: 10, RemoveStrayLetters: true));
        r.Recorder.NextAudio = FakeRecorder.Tone(1.0);
        r.Recorder.SnapshotAudio = FakeRecorder.Tone(0.6);
        r.Engine.Text = _ => "三部aと五部";
        r.Ptt.OnTalkKeyDown();
        for (int i = 0; i < 300 && r.Ptt.CurrentView.Interim is null; i++) await Task.Delay(10);
        Assert.Equal("三部と五部", r.Ptt.CurrentView.Interim);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
    }

    [Fact]
    public async Task はぐれ英字だけの発話は何も貼らず取り消し()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, RemoveStrayLetters: true));
        r.Engine.Text = _ => "h";
        r.Utter(1000);
        await r.Idle();
        Assert.Empty(r.Paster.Pasted);
        Assert.Equal(Outcome.Cancelled, Assert.Single(r.Reports).Outcome);
    }
}
