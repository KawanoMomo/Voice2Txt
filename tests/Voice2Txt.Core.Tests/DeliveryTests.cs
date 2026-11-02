using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>届け方(設定 delivery)。return は押した時点のウィンドウへ戻して貼り、クリップボードを元に戻す。</summary>
public class DeliveryTests
{
    private static readonly PttOptions Return = new(PasteSettleMs: 20, ReturnToTarget: true);
    private static string Final => $"len{Audio.SampleRate}";

    [Fact]
    public void 届け方の初期値は退避で_returnは画面の名前でも読める()
    {
        Assert.Equal(Deliveries.Evacuate, new AppSettings().Delivery);
        Assert.False(PttOptions.From(new AppSettings()).ReturnToTarget);
        Assert.True(PttOptions.From(new AppSettings { Delivery = "RETURN" }).ReturnToTarget);
        Assert.False(PttOptions.From(new AppSettings { Delivery = "どこか" }).ReturnToTarget); // 読めない値は初期値と同じ
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "delivery", "押した時のウィンドウへ戻して貼る"));
        Assert.Equal(Deliveries.Return, s.Delivery);
        Assert.Equal("return", SettingsSchema.Read(s, "delivery"));
        Assert.NotNull(SettingsSchema.Apply(s, "delivery", "メールで送る"));
        var t = new AppSettings();
        t.CopyFrom(s);
        Assert.Equal(Deliveries.Return, t.Delivery);
    }

    [Fact]
    public async Task returnなら録音中に前面が変わっても押した時のウィンドウへ貼り_前面とクリップボードを元に戻す()
    {
        await using var r = new Rig(Return);
        r.Clip.UserCopy("利用者がコピーした文");
        r.Ptt.OnTalkKeyDown();
        r.Fg.Window = 200; // 録音中に別のアプリへ移った
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(Outcome.Pasted, Assert.Single(r.Reports).Outcome);
        Assert.Equal((100, Final), Assert.Single(r.Paster.Pasted)); // 押した時のウィンドウ(100)に貼った
        Assert.Equal([100, 200], r.Fg.Activated);                   // 貼るために戻し、貼った後に元の前面へ
        Assert.Equal(200, r.Fg.Window);
        Assert.Equal("利用者がコピーした文", r.Clip.Text);           // クリップボードは貼る前の中身
        Assert.Equal(OverlayState.Pasted, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task returnで前面が変わっていなくても_貼った後にクリップボードを元に戻す()
    {
        await using var r = new Rig(Return);
        r.Clip.UserCopy("元の中身");
        r.Utter(1000);
        await r.Idle();
        Assert.Equal((100, Final), Assert.Single(r.Paster.Pasted));
        Assert.Empty(r.Fg.Activated);
        Assert.Equal("元の中身", r.Clip.Text);
    }

    [Fact]
    public async Task returnで前面に戻せなければ退避し_確定版をクリップボードに残す()
    {
        await using var r = new Rig(Return);
        r.Clip.UserCopy("元の中身");
        r.Fg.CanActivate = false;
        r.Ptt.OnTalkKeyDown();
        r.Fg.Window = 200;
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(Outcome.Evacuated, Assert.Single(r.Reports).Outcome);
        Assert.Empty(r.Paster.Pasted);
        Assert.Equal(Final, r.Clip.Text); // 確定版を失わない
        Assert.Equal(0, r.Clip.Restored);
    }

    [Fact]
    public async Task returnで貼った後に利用者がコピーしたものは戻さずに残す()
    {
        await using var r = new Rig(new PttOptions(PasteSettleMs: 300, ReturnToTarget: true));
        r.Clip.UserCopy("元の中身");
        r.Utter(1000);
        while (r.Paster.Pasted.Count == 0) await Task.Delay(5);
        r.Clip.UserCopy("貼った直後にコピーした文");
        await r.Idle();
        Assert.Equal("貼った直後にコピーした文", r.Clip.Text);
        Assert.Equal(0, r.Clip.Restored);
    }

    [Fact]
    public async Task 退避の届け方は今までどおり前面を移さず_クリップボードに確定版を残す()
    {
        await using var r = new Rig(new PttOptions(PasteSettleMs: 20));
        r.Clip.UserCopy("元の中身");
        r.Ptt.OnTalkKeyDown();
        r.Fg.Window = 200;
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(Outcome.Evacuated, Assert.Single(r.Reports).Outcome);
        Assert.Empty(r.Fg.Activated);
        Assert.Equal(Final, r.Clip.Text);
    }

    [Fact]
    public async Task returnの離してから届くまではクリップボードを戻す待ちを含めない()
    {
        await using var r = new Rig(new PttOptions(PasteSettleMs: 300, ReturnToTarget: true));
        r.Utter(1000);
        await r.Idle();
        Assert.Equal(0, Assert.Single(r.Reports).ReleaseToDeliverMs); // 偽の時計は進めていない
    }
}
