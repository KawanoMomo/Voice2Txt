using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>途中経過: 押下中にそれまでの音声から作り直す暫定の文字起こしは、オーバーレイにだけ出て、届かない。</summary>
public class InterimTests
{
    private static readonly float[] Half = FakeRecorder.Tone(0.6), Full = FakeRecorder.Tone(1.0);

    /// <summary>写し(0.6 秒)は「途中」、録音全体(1.0 秒)は「確定」と文字起こしするエンジン。</summary>
    private static Rig MakeRig(int intervalMs = 10)
    {
        var r = new Rig(new PttOptions(InterimIntervalMs: intervalMs));
        r.Recorder.NextAudio = Full;
        r.Recorder.SnapshotAudio = Half;
        r.Engine.Text = s => s.Length < Full.Length ? "途中" : "確定";
        return r;
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(5); }
        return cond();
    }

    [Fact]
    public async Task 押下中は途中経過がオーバーレイに出て_離すと確定版だけが届く()
    {
        await using var r = MakeRig();
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        Assert.Equal(OverlayState.Recording, r.Ptt.CurrentView.State);
        // 押下中は何も届かない(クリップボードにも入れない)
        Assert.Empty(r.Paster.Pasted);
        Assert.Null(r.Clip.Text);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("確定", Assert.Single(r.Paster.Pasted).Text);
        Assert.Equal("確定", Assert.Single(r.Reports).Text);
        Assert.Equal(OverlayState.Pasted, r.Ptt.CurrentView.State);
        Assert.Null(r.Ptt.CurrentView.Interim);
    }

    [Fact]
    public async Task CPUで動くとき途中経過を止めると_押下中に作り直さず確定版だけが届く()
    {
        await using var r = MakeRig();
        r.Ptt.InterimEnabled = Backends.InterimAllowed("Cpu", true);
        r.Ptt.OnTalkKeyDown();
        await Task.Delay(100);
        Assert.Equal(OverlayState.Recording, r.Ptt.CurrentView.State);
        Assert.Null(r.Ptt.CurrentView.Interim);
        Assert.Equal(0, r.Engine.InterimCalls);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("確定", Assert.Single(r.Paster.Pasted).Text);
        Assert.Equal(1, r.Engine.Calls);
    }

    [Fact]
    public async Task 途中経過は途中経過用の入口で_確定版は確定版の入口で文字起こしする()
    {
        // エンジンは途中経過のついでに、確定版が要りそうな拾い直しを先に済ませる(確定版の入口では先回りしない)
        await using var r = MakeRig();
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.True(r.Engine.InterimCalls >= 1);
        Assert.Equal(r.Engine.InterimCalls + 1, r.Engine.Calls);
        Assert.Equal(Full.Length, r.Engine.FinalLengths.Single());
    }

    [Fact]
    public async Task 離した後の処理中は最後の途中経過を出し続け_結末が出たら消す()
    {
        await using var r = MakeRig();
        var gate = new TaskCompletionSource();
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        r.Engine.Delay = s => s.Length == Full.Length ? gate.Task : Task.CompletedTask;
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        Assert.Equal(new OverlayView(OverlayState.Processing, 1, null, "途中"), r.Ptt.CurrentView);
        gate.SetResult();
        await r.Idle();
        Assert.Equal(new OverlayView(OverlayState.Pasted), r.Ptt.CurrentView);
    }

    [Fact]
    public async Task 離した後は途中経過を作り直さない()
    {
        await using var r = MakeRig();
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        int calls = r.Engine.Calls;
        await Task.Delay(100);
        Assert.Equal(calls, r.Engine.Calls);
    }

    [Fact]
    public async Task 前の発話の確定版を処理している間は途中経過を作らない()
    {
        await using var r = MakeRig();
        var gate = new TaskCompletionSource();
        r.Engine.Delay = s => s.Length == Full.Length ? gate.Task : Task.CompletedTask;
        r.Utter(1000);                 // 1 本目: 確定版の文字起こしで止まっている
        Assert.True(await Until(() => r.Engine.Calls == 1));
        r.Ptt.OnTalkKeyDown();         // 2 本目を押下中
        await Task.Delay(100);
        Assert.Equal(1, r.Engine.Calls);
        Assert.Equal(new OverlayView(OverlayState.ProcessingAndRecording, 1), r.Ptt.CurrentView);
        gate.SetResult();              // 1 本目が届いたら、2 本目の途中経過を作り始める
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        Assert.Equal(OverlayState.Recording, r.Ptt.CurrentView.State);
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal(["確定", "確定"], r.Paster.Pasted.Select(p => p.Text));
    }

    [Fact]
    public async Task 他のキーで取り消すと途中経過も消える()
    {
        await using var r = MakeRig();
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim == "途中"));
        r.Ptt.OnOtherKeyDown();
        await r.Idle();
        Assert.Null(r.Ptt.CurrentView.Interim);
        Assert.Empty(r.Paster.Pasted);
    }

    [Fact]
    public async Task 無音の間は途中経過を作らない()
    {
        await using var r = MakeRig();
        r.Recorder.SnapshotAudio = FakeRecorder.Silence(0.6);
        r.Ptt.OnTalkKeyDown();
        await Task.Delay(100);
        Assert.Equal(0, r.Engine.Calls);
        Assert.Null(r.Ptt.CurrentView.Interim);
    }

    [Fact]
    public async Task 途中経過を切ると押下中に文字起こししない()
    {
        await using var r = MakeRig(intervalMs: 0);
        r.Ptt.OnTalkKeyDown();
        await Task.Delay(100);
        Assert.Equal(0, r.Engine.Calls);
        Assert.Equal(new OverlayView(OverlayState.Recording), r.Ptt.CurrentView);
    }

    [Fact]
    public async Task 途中経過の文字起こしが失敗しても確定版は届く()
    {
        await using var r = MakeRig();
        r.Engine.Text = s => s.Length < Full.Length ? throw new InvalidOperationException("boom") : "確定";
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Engine.Calls >= 2));
        r.Clock.NowMs += 1000;
        r.Ptt.OnTalkKeyUp();
        await r.Idle();
        Assert.Equal("確定", Assert.Single(r.Paster.Pasted).Text);
    }

    [Fact]
    public async Task 途中経過も確定版と同じく言い淀みを除いて見せる()
    {
        await using var r = new Rig(new PttOptions(Fillers: Fillers.DefaultWords, InterimIntervalMs: 10));
        r.Recorder.NextAudio = Full;
        r.Recorder.SnapshotAudio = Half;
        r.Engine.Text = s => s.Length < Full.Length ? "ええ、明日やります" : "ええ、明日やります。";
        r.Ptt.OnTalkKeyDown();
        Assert.True(await Until(() => r.Ptt.CurrentView.Interim is not null));
        Assert.DoesNotContain("ええ", r.Ptt.CurrentView.Interim);
        Assert.Contains("明日やります", r.Ptt.CurrentView.Interim);
    }

    [Fact]
    public void 設定で途中経過を切り替えられる_初期値はオン()
    {
        Assert.True(new AppSettings().ShowInterim);
        Assert.Equal(PttOptions.DefaultInterimIntervalMs, PttOptions.From(new AppSettings()).InterimIntervalMs);
        Assert.Equal(0, PttOptions.From(new AppSettings { ShowInterim = false }).InterimIntervalMs);
    }

    [Fact]
    public void 欄に収まらない途中経過は頭を削って末尾を残す()
    {
        Func<string, bool> fits = s => s.Length <= 5;
        Assert.Equal("あいうえ", OverlayView.FitInterim("あいうえ", fits));
        Assert.Equal("…くけこ", OverlayView.FitInterim("あいうえおかきくけこ", s => s.Length <= 4));
        Assert.Equal("…けこ", OverlayView.FitInterim("あいうえおかきくけこ", s => s.Length <= 3));
    }
}
