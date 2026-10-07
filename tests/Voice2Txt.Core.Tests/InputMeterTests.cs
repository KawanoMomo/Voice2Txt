using Voice2Txt.Core;
using Voice2Txt.Core.Verification;
using Xunit;

namespace Voice2Txt.Core.Tests;

/// <summary>録音中の音量バーが録れている音の大きさに連動し、無音なら低く揃って止まること。</summary>
public class InputMeterTests
{
    [Fact]
    public void 無音は0で_バーは全部いちばん低く揃い_時間が経っても動かない()
    {
        Assert.Equal(0, InputMeter.Level(0));
        Assert.Equal(0, InputMeter.Level(Audio.TailRms(FakeRecorder.Silence(1), Audio.SampleRate / 2)));
        for (int tick = 0; tick < 50; tick++)
            Assert.All(InputMeter.BarHeights(0, tick), h => Assert.Equal(InputMeter.MinBarHeight, h));
    }

    [Fact]
    public void 声の大きさの音は高く_小さい音は低い()
    {
        double loud = InputMeter.Level(Audio.TailRms(FakeRecorder.Tone(1, 0.2f), 8000));   // 約 -17 dBFS
        double quiet = InputMeter.Level(Audio.TailRms(FakeRecorder.Tone(1, 0.003f), 8000)); // 約 -53 dBFS
        Assert.InRange(loud, 0.9, 1.0);
        Assert.InRange(quiet, 0.05, 0.3);
        Assert.True(InputMeter.BarHeights(loud, 7).Sum() > InputMeter.BarHeights(quiet, 7).Sum());
        Assert.Equal(1, InputMeter.Level(1.0));
        Assert.Equal(0, InputMeter.Level(1e-6)); // -120 dBFS は床より下
    }

    [Fact]
    public void バーの高さは最小と最大の間で_中央がいちばん高い()
    {
        for (int tick = 0; tick < 30; tick++)
        {
            var h = InputMeter.BarHeights(1, tick);
            Assert.Equal(InputMeter.BarCount, h.Length);
            Assert.All(h, x => Assert.InRange(x, InputMeter.MinBarHeight, InputMeter.MaxBarHeight));
            Assert.True(h[2] >= h[0] && h[2] >= h[4]);
        }
    }

    [Fact]
    public void 上がるときはすぐ追い_下がるときは少しずつ()
    {
        Assert.Equal(0.8, InputMeter.Follow(0.1, 0.8));
        Assert.Equal(0.8 - InputMeter.FallPerTick, InputMeter.Follow(0.8, 0), 6);
        double v = 1;
        for (int i = 0; i < 10; i++) v = InputMeter.Follow(v, 0);
        Assert.Equal(0, v);
    }

    [Fact]
    public void 直前の0_1秒だけを見る()
    {
        var a = FakeRecorder.Tone(1, 0.2f).Concat(FakeRecorder.Silence(1)).ToArray();
        Assert.True(Audio.TailRms(a, Audio.SampleRate) > 0.1);                          // 発話の終わり
        Assert.Equal(0, Audio.TailRms(a, Audio.SampleRate + Audio.MeterWindow));        // 無音に入って 0.1 秒
        Assert.Equal(0, Audio.TailRms(a, 0));
        Assert.Equal(0, Audio.TailRms([], 10));
    }

    [Fact]
    public async Task 押している間だけ録れている音の大きさを返す()
    {
        await using var rig = new Rig();
        rig.Recorder.InputRms = 0.2;
        Assert.Equal(0, rig.Ptt.InputLevel);               // 押す前
        rig.Recorder.StartImmediately = false;
        rig.Ptt.OnTalkKeyDown();
        Assert.Equal(0, rig.Ptt.InputLevel);               // 準備中(まだ音が届いていない)
        rig.Recorder.PendingStart!();
        Assert.InRange(rig.Ptt.InputLevel, 0.9, 1.0);      // 録音中
        rig.Recorder.InputRms = 0;
        Assert.Equal(0, rig.Ptt.InputLevel);               // 無音
        rig.Clock.NowMs += 1000;
        rig.Ptt.OnTalkKeyUp();
        rig.Recorder.InputRms = 0.2;
        Assert.Equal(0, rig.Ptt.InputLevel);               // 離した後
        await rig.Idle();
    }

    [Fact]
    public void shotの期待_名前_状態_音量バーの範囲()
    {
        var r = new VerifyResult { Completed = true };
        r.Shots.Add(new ShotRecord { Name = "発話中", State = "録音中", Meter = 0.8, Screenshot = "shots/01.png" });
        r.Shots.Add(new ShotRecord { Name = "無音中", State = "録音中", Meter = 0, Screenshot = "shots/02.png" });
        var ok = new Expectation
        {
            Shots = [new() { Name = "発話中", State = "録音中", MinMeter = 0.5 }, new() { Name = "無音中", MaxMeter = 0.05 }],
        };
        Assert.Empty(Evaluator.Check(ok, r, _ => true));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "発話中", MaxMeter = 0.05 }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "無音中", MinMeter = 0.5 }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "発話中", State = "処理中" }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "無い" }] }, r));
        Assert.Single(Evaluator.Check(new Expectation { Shots = [new() { Name = "発話中" }] }, r, _ => false));
    }
}
