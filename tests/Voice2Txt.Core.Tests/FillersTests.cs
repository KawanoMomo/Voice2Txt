using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class FillersTests
{
    private static string Clean(string s) => Fillers.Remove(s, Fillers.DefaultWords).Text;

    [Theory]
    [InlineData("申請書は、ええと、ええと、総務に出してください。", "申請書は、総務に出してください。")]
    [InlineData("ええ、それで大丈夫です。", "それで大丈夫です。")]
    [InlineData("ああ、そうですか。", "そうですか。")]
    [InlineData("えっと、明日の会議は三時からです。", "明日の会議は三時からです。")]
    [InlineData("資料は、あの、三部、ええと、五部印刷しておいてください。", "資料は、三部、五部印刷しておいてください。")]
    [InlineData("その、つまり、予算の確認をお願いします。", "つまり、予算の確認をお願いします。")]
    [InlineData("まあ、いいでしょう。", "いいでしょう。")]
    [InlineData("えーと今日は晴れです。", "今日は晴れです。")]
    [InlineData("うーん、どうしましょう。", "どうしましょう。")]
    [InlineData("あのー、すみません。", "すみません。")]
    [InlineData("えーっと、ですね。", "ですね。")]
    [InlineData("エート、資料を送ります。", "資料を送ります。")]
    [InlineData("確認しました、ええ。", "確認しました。")]
    [InlineData("はい。ええ。そうです。", "はい。そうです。")]
    [InlineData("えー 明日 行きます", "明日 行きます")]
    public void 言い淀みを取り除き_読点の跡を整える(string input, string expected) => Assert.Equal(expected, Clean(input));

    [Theory]
    [InlineData("あの人に頼みました。")]
    [InlineData("そのため、開始が遅れます。")]
    [InlineData("その一方で、課題も残ります。")]
    [InlineData("ああいう言い方はやめましょう。")]
    [InlineData("まあまあの出来です。")]
    [InlineData("あの町の景色は心の中に残っています。")]
    [InlineData("考えーと言った。")]
    [InlineData("見ええと書く。")]
    [InlineData("今日の会議の資料を送ります。")]
    public void 言い淀みでない語は残す(string input)
    {
        var (text, removed) = Fillers.Remove(input, Fillers.DefaultWords);
        Assert.Equal(input, text);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void 取り除いた数を返す() =>
        Assert.Equal(2, Fillers.Remove("申請書は、ええと、ええと、総務に出してください。", Fillers.DefaultWords).Removed);

    [Theory]
    [InlineData("ええと。")]
    [InlineData("ええと、あの、")]
    [InlineData("うーん")]
    public void 言い淀みだけなら空になる(string input) => Assert.Equal("", Clean(input));

    [Fact]
    public void 語の一覧は設定で変えられる()
    {
        Assert.Equal("えっと、資料です。", Fillers.Remove("なんか、えっと、資料です。", ["なんか"]).Text);
        Assert.Equal("ええと、資料です。", Fillers.Remove("ええと、資料です。", []).Text);
    }

    [Fact]
    public void 既定の設定は言い淀みを取り除く_止めると渡さない()
    {
        var s = new AppSettings();
        Assert.True(s.RemoveFillers);
        Assert.Equal(Fillers.DefaultWords, PttOptions.From(s).Fillers);
        s.RemoveFillers = false;
        Assert.Null(PttOptions.From(s).Fillers);
    }

    [Fact]
    public void 設定ファイルに無ければ既定_書けば読める()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ffv2t-fillers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{ "talkKey": "RControlKey" }""");
            var old = AppSettings.LoadOrCreate(path);
            Assert.True(old.RemoveFillers);
            Assert.Equal(Fillers.DefaultWords, old.Fillers);

            File.WriteAllText(path, """{ "removeFillers": false, "fillers": ["なんか"] }""");
            var s = AppSettings.LoadOrCreate(path);
            Assert.False(s.RemoveFillers);
            Assert.Equal(["なんか"], s.Fillers);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 届ける確定版から言い淀みを取り除く()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords));
        r.Engine.Text = _ => "ええと、総務に出してください。";
        r.Utter(1000);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(Outcome.Pasted, rep.Outcome);
        Assert.Equal("総務に出してください。", rep.Text);
        Assert.Equal(1, rep.FillersRemoved);
        Assert.Equal("総務に出してください。", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 設定で止めると文字起こしのまま届ける()
    {
        await using var r = new Rig(PttOptions.From(new AppSettings { RemoveFillers = false }));
        r.Engine.Text = _ => "ええと、総務に出してください。";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal("ええと、総務に出してください。", Assert.Single(r.Paster.Pasted).Text);
        Assert.Equal(0, Assert.Single(r.Reports).FillersRemoved);
    }

    [Fact]
    public async Task 言い淀みだけの発話は何も貼らず取り消し()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords));
        r.Engine.Text = _ => "えーと。";
        r.Utter(1000);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal((Outcome.Cancelled, CancelReason.NoText), (rep.Outcome, rep.Reason));
        Assert.Empty(r.Paster.Pasted);
        Assert.Null(r.Clip.Text);
    }

    [Fact]
    public void 台本の_notContains_に残った語を失敗にする()
    {
        var e = new Expectation
        {
            Deliveries = [new() { Text = "総務に出してください。", MinSimilarity = 0.5, NotContains = ["ええと"] }],
            Textbox = new() { NotContains = ["ええと"] },
        };
        var r = new VerifyResult
        {
            Completed = true,
            Deliveries = [new() { Seq = 1, To = "textbox", Text = "ええと、総務に出してください。" }],
            Textbox = "ええと、総務に出してください。",
        };
        var fails = Evaluator.Check(e, r);
        Assert.Contains(fails, f => f.Contains("1 件目に「ええと」が残っている"));
        Assert.Contains(fails, f => f.Contains("テキスト欄に「ええと」が残っている"));
        r.Deliveries[0].Text = r.Textbox = "総務に出してください。";
        Assert.Empty(Evaluator.Check(e, r));
    }
}
