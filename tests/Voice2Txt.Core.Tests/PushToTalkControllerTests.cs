using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class PushToTalkControllerTests
{
    [Fact]
    public async Task 押して離すと確定版が貼り付け先ウィンドウへ貼られる()
    {
        await using var r = new Rig();
        r.Utter(1000);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(Outcome.Pasted, rep.Outcome);
        Assert.Equal((100, $"len{Audio.SampleRate}"), Assert.Single(r.Paster.Pasted));
        Assert.Equal($"len{Audio.SampleRate}", r.Clip.Text);
    }

    [Fact]
    public async Task 状態は_準備中_録音中_処理中_貼り付け完了_の順に変わる()
    {
        await using var r = new Rig();
        r.Recorder.StartImmediately = false;
        r.Ptt.OnTalkKeyDown();
        Assert.Equal(OverlayState.Preparing, r.Ptt.CurrentView.State);
        r.Recorder.PendingStart!();
        Assert.Equal(OverlayState.Recording, r.Ptt.CurrentView.State);
        r.Clock.NowMs += 1000;
        r.Engine.Delay = _ => Task.Delay(100);
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal([OverlayState.Preparing, OverlayState.Recording, OverlayState.Processing, OverlayState.Pasted], r.States());
    }

    [Fact]
    public async Task 押下が短すぎると取り消しで_何も貼らない()
    {
        await using var r = new Rig();
        r.Utter(299);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal((Outcome.Cancelled, CancelReason.TooShort), (rep.Outcome, rep.Reason));
        Assert.Empty(r.Paster.Pasted);
        Assert.Equal(0, r.Engine.Calls);
        Assert.Equal(OverlayState.Cancelled, r.Ptt.CurrentView.State);
        Assert.Equal(1, r.Recorder.Aborted);
    }

    [Fact]
    public async Task 押下の閾値は設定で変えられる()
    {
        await using var r = new Rig(new PttOptions(MinPressSeconds: 0.05));
        r.Utter(100);
        await r.Idle();
        Assert.Equal(Outcome.Pasted, Assert.Single(r.Reports).Outcome);
    }

    [Fact]
    public async Task 押下中に別のキーが押されたら取り消し_離しても何も起きない()
    {
        await using var r = new Rig();
        r.Ptt.OnTalkKeyDown();
        r.Clock.NowMs += 800;
        r.Ptt.OnOtherKeyDown();
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(CancelReason.OtherKey, rep.Reason);
        Assert.Empty(r.Paster.Pasted);
        Assert.Null(r.Clip.Text);
        Assert.Equal(OverlayState.Hidden, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task 無音はWhisperに渡さず取り消し()
    {
        await using var r = new Rig();
        r.Utter(2000, FakeRecorder.Silence(2));
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(CancelReason.Silence, rep.Reason);
        Assert.Equal(0, r.Engine.Calls);
        Assert.Empty(r.Paster.Pasted);
        Assert.Equal(OverlayState.CancelledSilence, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task 文字起こしが空なら何も貼らない()
    {
        await using var r = new Rig();
        r.Engine.Text = _ => "  ";
        r.Utter(1000);
        await r.Idle();
        Assert.Equal(CancelReason.NoText, Assert.Single(r.Reports).Reason);
        Assert.Empty(r.Paster.Pasted);
    }

    [Fact]
    public async Task 貼る時点で前面が変わっていたら退避_クリップボードに残してCtrlVを送らない()
    {
        await using var r = new Rig();
        var gate = new TaskCompletionSource();
        r.Engine.Delay = _ => gate.Task;
        r.Utter(1000);
        r.Fg.Window = 200; // 処理中にウィンドウを切り替えた
        gate.SetResult();
        await r.Idle();
        Assert.Equal(Outcome.Evacuated, Assert.Single(r.Reports).Outcome);
        Assert.Empty(r.Paster.Pasted);
        Assert.Equal($"len{Audio.SampleRate}", r.Clip.Text);
        Assert.Equal(OverlayState.Evacuated, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task 処理中に次の発話を録音でき_録音した順に届く_後の発話が先に終わっても追い越さない()
    {
        await using var r = new Rig();
        var first = new TaskCompletionSource();
        r.Engine.Delay = s => s.Length == Audio.SampleRate ? first.Task : Task.CompletedTask;
        r.Utter(1000, FakeRecorder.Tone(1.0));          // 1 件目(遅い)
        r.Recorder.NextAudio = FakeRecorder.Tone(0.5);
        r.Ptt.OnTalkKeyDown();                          // 処理中に 2 件目を録音
        Assert.Equal(new OverlayView(OverlayState.ProcessingAndRecording, 1), r.Ptt.CurrentView);
        r.Clock.NowMs += 600;
        r.Ptt.OnTalkKeyUp();
        await Task.Delay(100);
        Assert.Empty(r.Paster.Pasted);                  // 2 件目は 1 件目を追い越さない
        first.SetResult();
        await r.Idle();
        Assert.Equal([$"len{Audio.SampleRate}", $"len{Audio.SampleRate / 2}"], r.Paster.Pasted.Select(p => p.Text));
        Assert.Equal([1, 2], r.Reports.Select(x => x.Seq).ToArray());
    }

    [Fact]
    public async Task 処理待ちの件数をオーバーレイに出す()
    {
        await using var r = new Rig();
        var gate = new TaskCompletionSource();
        r.Engine.Delay = _ => gate.Task;
        r.Utter(1000);
        r.Utter(1000);
        r.Ptt.OnTalkKeyDown();
        Assert.Equal(new OverlayView(OverlayState.ProcessingAndRecording, 2), r.Ptt.CurrentView);
        r.Ptt.OnOtherKeyDown();
        Assert.Equal(new OverlayView(OverlayState.Processing, 2), r.Ptt.CurrentView);
        gate.SetResult();
        await r.Idle();
        Assert.Equal(2, r.Paster.Pasted.Count);
    }

    [Fact]
    public async Task モデル準備中はモデル準備中を出し_録音しない()
    {
        await using var r = new Rig(modelReady: false);
        r.Ptt.SetModelStatus(false, "ダウンロード 42%");
        r.Ptt.OnTalkKeyDown();
        Assert.Equal(new OverlayView(OverlayState.ModelPreparing, 0, "ダウンロード 42%"), r.Ptt.CurrentView);
        Assert.Contains("ダウンロード 42%", r.Ptt.CurrentView.Text("右Ctrl"));
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(CancelReason.ModelNotReady, Assert.Single(r.Reports).Reason);
        Assert.Equal(0, r.Engine.Calls);
        Assert.Equal(OverlayState.Hidden, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task 貼り付け先ウィンドウは押した時点の前面ウィンドウ()
    {
        await using var r = new Rig();
        r.Fg.Window = 300;
        r.Ptt.OnTalkKeyDown();
        r.Fg.Window = 400; // 押下中に切り替え
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(Outcome.Evacuated, Assert.Single(r.Reports).Outcome);
    }

    [Fact]
    public async Task 時間を記録する_マイクが開くまで_離してから届くまで()
    {
        await using var r = new Rig();
        r.Recorder.StartImmediately = false;
        r.Ptt.OnTalkKeyDown();
        r.Clock.NowMs += 120;
        r.Recorder.PendingStart!();
        r.Clock.NowMs += 1000;
        r.Engine.Delay = _ => { r.Clock.NowMs += 700; return Task.CompletedTask; };
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(120, rep.MicOpenMs);
        Assert.Equal(1120, rep.HeldMs);
        Assert.Equal(700, rep.ReleaseToDeliverMs);
    }
}
