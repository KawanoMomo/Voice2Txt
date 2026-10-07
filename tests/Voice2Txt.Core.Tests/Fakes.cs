using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

internal sealed class FakeClock : IClock
{
    public long NowMs { get; set; }
}

internal sealed class FakeRecorder : IRecorder
{
    public float[] NextAudio = Tone(1.0);
    public bool StartImmediately = true;
    public Action? PendingStart;
    public int Aborted;
    /// <summary>録音中の音の大きさ(IRecording.InputRms が返す値)。</summary>
    public double InputRms;
    /// <summary>押下中の写し(IRecording.Snapshot が返す音声)。null なら録音全体。</summary>
    public float[]? SnapshotAudio;

    public IRecording Start(Action onStarted)
    {
        if (StartImmediately) onStarted(); else PendingStart = onStarted;
        return new Rec(this, NextAudio);
    }

    private sealed class Rec(FakeRecorder r, float[] audio) : IRecording
    {
        public Task<float[]> StopAsync() => Task.FromResult(audio);
        public void Abort() => r.Aborted++;
        public double InputRms => r.InputRms;
        public float[] Snapshot() => r.SnapshotAudio ?? audio;
    }

    public static float[] Tone(double seconds, float amp = 0.2f) =>
        Enumerable.Range(0, (int)(seconds * Audio.SampleRate)).Select(i => amp * (float)Math.Sin(i * 2 * Math.PI * 440 / Audio.SampleRate)).ToArray();

    public static float[] Silence(double seconds) => new float[(int)(seconds * Audio.SampleRate)];
}

internal sealed class FakeForeground : IForegroundWindow
{
    public nint Window = 100;
    public nint Current() => Window;
}

/// <summary>FailTimes 回だけ(負なら常に)他アプリが開いたままのように投げる。</summary>
internal sealed class FakeClipboard : IClipboard
{
    public string? Text;
    public int FailTimes;
    public int Attempts;

    public void SetText(string text)
    {
        int n = Interlocked.Increment(ref Attempts);
        if (FailTimes < 0 || n <= FailTimes) throw new System.Runtime.InteropServices.ExternalException("クリップボードを開けません");
        Text = text;
    }
}

/// <summary>貼り付け先: Ctrl+V が来たら、その時点のクリップボードの中身を前面ウィンドウに貼ったことにする。</summary>
internal sealed class FakePaster(FakeClipboard clip, FakeForeground fg) : IPasteSender
{
    public readonly List<(nint Window, string Text)> Pasted = [];
    public void SendPaste() { lock (Pasted) Pasted.Add((fg.Window, clip.Text!)); }
}

/// <summary>遅い貼り付け先: Ctrl+V を受けてから ReadDelayMs 後にクリップボードを読む(本物の貼り付け先は自分の入力を処理した時に読む)。</summary>
internal sealed class LatePaster(FakeClipboard clip, int readDelayMs) : IPasteSender
{
    public readonly List<string> Pasted = [];
    private readonly List<Task> _reads = [];

    public void SendPaste()
    {
        var t = Task.Run(async () =>
        {
            await Task.Delay(readDelayMs);
            lock (Pasted) Pasted.Add(clip.Text!);
        });
        lock (_reads) _reads.Add(t);
    }

    public Task Drain() { lock (_reads) return Task.WhenAll(_reads.ToArray()); }
}

/// <summary>音声の長さ(サンプル数)から文字列を作る。発話ごとの遅延を指定できる。</summary>
internal sealed class FakeTranscriber : ITranscriber
{
    public Func<float[], string> Text = s => $"len{s.Length}";
    public Func<float[], Task>? Delay;
    public int Calls, InterimCalls;
    public readonly System.Collections.Concurrent.ConcurrentQueue<int> FinalLengths = new();

    public async Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        FinalLengths.Enqueue(samples16k.Length);
        if (Delay is not null) await Delay(samples16k);
        return Text(samples16k);
    }

    public async Task<string> TranscribeInterimAsync(float[] samples16k, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Interlocked.Increment(ref InterimCalls);
        if (Delay is not null) await Delay(samples16k);
        return Text(samples16k);
    }
}

internal sealed class Rig : IAsyncDisposable
{
    public readonly FakeClock Clock = new();
    public readonly FakeRecorder Recorder = new();
    public readonly FakeForeground Fg = new();
    public readonly FakeClipboard Clip = new();
    public readonly FakePaster Paster;
    public readonly FakeTranscriber Engine = new();
    public readonly PushToTalkController Ptt;
    public readonly List<UtteranceReport> Reports = [];
    public readonly List<OverlayView> Views = [];

    public Rig(PttOptions? o = null, bool modelReady = true)
    {
        Paster = new FakePaster(Clip, Fg);
        Ptt = new PushToTalkController(new PttDependencies(Recorder, Fg, Clip, Paster, Engine, Clock), o ?? new PttOptions());
        Ptt.Finished += r => { lock (Reports) Reports.Add(r); };
        Ptt.OverlayChanged += v => { lock (Views) Views.Add(v); };
        if (modelReady) Ptt.SetModelStatus(true);
    }

    /// <summary>押して、ms 後に離す。</summary>
    public void Utter(long heldMs, float[]? audio = null)
    {
        if (audio is not null) Recorder.NextAudio = audio;
        Ptt.OnTalkKeyDown();
        Clock.NowMs += heldMs;
        Ptt.OnTalkKeyUp();
    }

    public async Task Idle() => Assert.True(await Ptt.WaitIdleAsync(TimeSpan.FromSeconds(5)));

    public List<OverlayState> States() { lock (Views) return Views.Select(v => v.State).ToList(); }

    public ValueTask DisposeAsync() => Ptt.DisposeAsync();
}
