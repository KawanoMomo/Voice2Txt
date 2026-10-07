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
    public async Task しきい値を下回る小さい声も無音として取り消さず届ける()
    {
        // 感度の低いマイク・小さい声: 最も大きい所でも無音のしきい値(0.01)に届かないが、マイクの雑音よりはっきり大きい
        await using var r = new Rig();
        r.Utter(1500, AudioTests.Noisy([.. FakeRecorder.Silence(0.3), .. FakeRecorder.Tone(0.8, 0.006f), .. FakeRecorder.Silence(0.4)], 0.0003));
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal((Outcome.Pasted, CancelReason.None), (rep.Outcome, rep.Reason));
        Assert.Equal(1, r.Engine.Calls);
        Assert.InRange(rep.PeakRms!.Value, 0.004, 0.0099);
    }

    [Fact]
    public async Task 無音の取り消しでも録音の大きさを結末に残す()
    {
        await using var r = new Rig();
        r.Utter(2000, AudioTests.Noisy(FakeRecorder.Silence(2), 0.0003));
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal(CancelReason.Silence, rep.Reason);
        Assert.Equal(0, r.Engine.Calls);
        Assert.InRange(rep.PeakRms!.Value, 0.0001, 0.001);
    }

    [Fact]
    public async Task 前後の長い無音は詰めてからWhisperに渡す()
    {
        await using var r = new Rig();
        float[]? got = null;
        r.Engine.Text = s => { got = s; return "ok"; };
        r.Utter(25000, [.. FakeRecorder.Silence(10), .. FakeRecorder.Tone(2), .. FakeRecorder.Silence(12)]);
        await r.Idle();
        Assert.Equal(Outcome.Pasted, Assert.Single(r.Reports).Outcome);
        Assert.NotNull(got);
        Assert.InRange(got!.Length, Audio.SampleRate * 2, (int)(Audio.SampleRate * (2 + 2 * Audio.SpeechPadSeconds + 0.1)));
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

    private static readonly PttOptions QuickRetry = new(ClipboardRetryMs: 200, ClipboardRetryIntervalMs: 1);

    [Fact]
    public async Task クリップボードが一時的に使えなくても粘って入れ_貼り付ける()
    {
        await using var r = new Rig(QuickRetry);
        r.Clip.FailTimes = 3;
        r.Utter(1000);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal((Outcome.Pasted, CancelReason.None), (rep.Outcome, rep.Reason));
        Assert.Equal([$"len{Audio.SampleRate}"], r.Paster.Pasted.Select(p => p.Text));
        Assert.Equal(4, r.Clip.Attempts);
    }

    [Fact]
    public async Task クリップボードに入ってから窓が変わっていれば退避()
    {
        await using var r = new Rig(QuickRetry);
        r.Clip.FailTimes = 3;
        r.Fg.Window = 100;
        r.Ptt.OnTalkKeyDown();
        r.Fg.Window = 200;
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(Outcome.Evacuated, Assert.Single(r.Reports).Outcome);
        Assert.Equal($"len{Audio.SampleRate}", r.Clip.Text);
        Assert.Equal(OverlayState.Evacuated, r.Ptt.CurrentView.State);
    }

    [Fact]
    public async Task クリップボードに入れられなければ退避と出さず_入力失敗を知らせる_CtrlVも送らない()
    {
        await using var r = new Rig(QuickRetry);
        r.Clip.FailTimes = -1;
        r.Utter(1000);
        await r.Idle();
        var rep = Assert.Single(r.Reports);
        Assert.Equal((Outcome.Failed, CancelReason.ClipboardBusy), (rep.Outcome, rep.Reason));
        Assert.Null(rep.Text);                          // ログ・結果に本文を残さない
        Assert.NotNull(rep.Error);
        Assert.Empty(r.Paster.Pasted);
        Assert.Null(r.Clip.Text);
        Assert.DoesNotContain(OverlayState.Evacuated, r.States());
        Assert.Equal(OverlayState.DeliveryFailed, r.Ptt.CurrentView.State);
        Assert.DoesNotContain("Ctrl+V", r.Ptt.CurrentView.Text("右Ctrl"));
        Assert.True(r.Clip.Attempts >= 3);              // すぐには捨てない
    }

    [Fact]
    public async Task クリップボードが使えない間も次の発話は順に待ち_使えるようになれば届く()
    {
        await using var r = new Rig(QuickRetry);
        r.Clip.FailTimes = -1;
        var gate = new TaskCompletionSource();
        r.Engine.Delay = s => s.Length == Audio.SampleRate / 2 ? gate.Task : Task.CompletedTask;
        r.Utter(1000, FakeRecorder.Tone(1.0));
        r.Utter(600, FakeRecorder.Tone(0.5));
        await Task.Delay(600);
        r.Clip.FailTimes = 0;                           // 他アプリがクリップボードを放した
        gate.SetResult();
        await r.Idle();
        Assert.Equal([1, 2], r.Reports.Select(x => x.Seq).ToArray());
        Assert.Equal(Outcome.Failed, r.Reports[0].Outcome);
        Assert.Equal(Outcome.Pasted, r.Reports[1].Outcome);
        Assert.Equal([$"len{Audio.SampleRate / 2}"], r.Paster.Pasted.Select(p => p.Text));
    }

    [Fact]
    public void 入力失敗の文言は退避と違い_貼れると言わない()
    {
        var v = new OverlayView(OverlayState.DeliveryFailed);
        Assert.Equal("入力失敗", v.Label);
        Assert.Contains("クリップボード", v.Text("右Ctrl"));
        Assert.DoesNotContain("貼れます", v.Text("右Ctrl"));
        Assert.True(v.TransientMs >= 4000);
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

    /// <summary>貼り付け先が Ctrl+V を遅れて読む。処理待ちの次の発話がその前にクリップボードを上書きすると、前の発話が欠けて次が 2 回貼られる。</summary>
    [Fact]
    public async Task 処理待ちの連投でも_貼り付け先が読み終える前に次の確定版でクリップボードを上書きしない()
    {
        var clock = new FakeClock(); var rec = new FakeRecorder(); var fg = new FakeForeground(); var clip = new FakeClipboard();
        var paster = new LatePaster(clip, readDelayMs: 150);
        var engine = new FakeTranscriber();
        var first = new TaskCompletionSource();
        engine.Delay = s => s.Length == Audio.SampleRate ? first.Task : Task.Delay(80);
        await using var ptt = new PushToTalkController(new PttDependencies(rec, fg, clip, paster, engine, clock), new PttOptions());
        ptt.SetModelStatus(true);
        foreach (var sec in new[] { 1.0, 0.5, 0.25 })   // 1 本目の処理中に 2・3 本目を録り終える(処理待ち 2 件)
        {
            rec.NextAudio = FakeRecorder.Tone(sec);
            ptt.OnTalkKeyDown(); clock.NowMs += 1000; ptt.OnTalkKeyUp();
        }
        first.SetResult();
        Assert.True(await ptt.WaitIdleAsync(TimeSpan.FromSeconds(10)));
        await paster.Drain();
        Assert.Equal([$"len{Audio.SampleRate}", $"len{Audio.SampleRate / 2}", $"len{Audio.SampleRate / 4}"], paster.Pasted);
    }

    [Fact]
    public async Task 処理待ちが無ければ_貼り付けの後の待ちで届くのは遅れない()
    {
        await using var r = new Rig(new PttOptions(PasteSettleMs: 2000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Utter(1000);
        await r.Idle();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"1 本目 {sw.ElapsedMilliseconds} ms");   // 前に貼ったものが無ければ待たない
        await Task.Delay(2100);                         // 前の貼り付けから間合いより後に話した次の発話も待たない
        sw.Restart();
        r.Utter(1000);
        await r.Idle();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"2 本目 {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, r.Paster.Pasted.Count);
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
