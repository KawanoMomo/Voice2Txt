using System.Diagnostics;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt;

/// <summary>
/// 検証モード: マイクの代わりに音声ファイルを流し込み、トークキーの押下・離しを台本どおりに指示し、
/// 確定版をアプリ自身が開く検証用のテキスト欄へ届ける。状態が変わるごとにオーバーレイのスクリーンショットを撮り、
/// 結果を &lt;out&gt;/result.json に書く。本物のマイク・クリップボード・キーボードフックは使わない。
/// 台本のキーはキーボードフックと同じ判定(<see cref="TalkKeyFilter"/>)に通し、素通ししたキー・合成して送ったキーを結果に残す。
/// 設定は台本の settings だけを使う(%APPDATA% の settings.json は読まない)。
/// 貼り付け先の判定は仮の前面(<see cref="VirtualForeground"/>)で行うが、オーバーレイがフォーカスを奪っていないかは
/// 本物の前面ウィンドウを一定間隔と状態が変わるたびに調べて結果に残す。
/// </summary>
internal sealed class VerifyHost : ApplicationContext
{
    private readonly Scenario _sc;
    private readonly string _scenarioPath, _out, _shots;
    private readonly AppSettings _settings;
    private readonly Keys _talkKey;
    private readonly TalkKeyFilter _keys;
    private readonly VerifyTextForm _textForm;
    private readonly OverlayForm _overlay;
    private readonly VerifyRecorder _recorder = new();
    private readonly VirtualForeground _fg;
    private readonly MemoryClipboard _clip = new();
    private readonly PushToTalkController _ptt;
    private readonly DeferredTranscriber _engine = new();
    private readonly VerifyResult _result;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly TaskCompletionSource _modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _shotNo;
    private readonly System.Windows.Forms.Timer _fgProbe = new() { Interval = 25 };

    public VerifyHost(string scenarioPath, string outDir)
    {
        _scenarioPath = scenarioPath;
        _sc = Scenario.Load(scenarioPath);
        _out = Path.GetFullPath(outDir);
        _shots = Path.Combine(_out, "shots");
        Directory.CreateDirectory(_shots);
        AppLog.Path = Path.Combine(_out, "verify.log");
        _result = new VerifyResult { Scenario = _sc.Name };
        _settings = _sc.Settings ?? new AppSettings();
        _talkKey = TalkKeys.Parse(_settings.TalkKey);
        _keys = new TalkKeyFilter((int)_talkKey);
        if (TalkKeys.InvalidWarning(_settings.TalkKey) is { } warn)
        {
            _result.Warnings.Add(warn);
            AppLog.Write($"talkkey-invalid name={_settings.TalkKey} fallback={TalkKeys.Default}");
        }

        _textForm = new VerifyTextForm();
        _textForm.Show();
        _overlay = new OverlayForm(TalkKeys.DisplayName(_talkKey));
        _ = _overlay.Handle;
        _fg = new VirtualForeground(_textForm.Handle);

        _ptt = new PushToTalkController(
            new PttDependencies(_recorder, _fg, _clip, new TextBoxPaster(_textForm, _clip), _engine, new SystemClock()),
            PttOptions.From(_settings));
        _ptt.OverlayChanged += v => _overlay.BeginInvoke(() => OnOverlay(v));
        _ptt.Finished += OnFinished;
        _fgProbe.Tick += (_, _) => ProbeForeground();
        _fgProbe.Start();

        _ = RunAsync();
    }

    private void OnOverlay(OverlayView v)
    {
        _overlay.Apply(v);
        var rec = new StateRecord { AtMs = _sw.ElapsedMilliseconds, State = v.Label, Text = v.State == OverlayState.Hidden ? "" : v.Text(TalkKeys.DisplayName(_talkKey)) };
        if (v.State != OverlayState.Hidden)
        {
            _overlay.Refresh();
            Thread.Sleep(40);
            var name = $"{++_shotNo:00}-{Safe(v.Label)}.png";
            rec.Capture = _overlay.SaveScreenshot(Path.Combine(_shots, name));
            rec.Screenshot = "shots/" + name;
        }
        rec.Foreground = ProbeForeground();
        lock (_result) _result.States.Add(rec);
        AppLog.Write($"state {v.Label} capture={rec.Capture}");
    }

    /// <summary>台本のキー 1 つをトークキーの判定に通す(キーボードフックと同じ)。名前の省略はトークキー。</summary>
    private void Key(string? name, bool down)
    {
        Keys key = name is null ? _talkKey : TalkKeys.TryParse(name, out var k) ? k : throw new InvalidDataException($"キーの名前を読めない: {name}");
        var v = _keys.OnKey((int)key, down);
        string ev = $"{key} {(down ? "down" : "up")}";
        AppLog.Write($"key {ev} swallow={v.Swallow} signal={v.Signal}");
        lock (_result)
        {
            if (!v.Swallow) _result.PassedKeys.Add(ev);
            foreach (var vk in v.InjectDown) _result.SentKeys.Add($"{(Keys)vk} down");
        }
        switch (v.Signal)
        {
            case TalkKeySignal.TalkDown: _ptt.OnTalkKeyDown(); break;
            case TalkKeySignal.TalkUp: _ptt.OnTalkKeyUp(); break;
            case TalkKeySignal.OtherKeyWhileTalk: _ptt.OnOtherKeyDown(); break;
        }
    }

    /// <summary>本物の前面ウィンドウを調べて数える(UI スレッド)。</summary>
    private string ProbeForeground()
    {
        nint h = Native.GetForegroundWindow();
        string who = h == _overlay.Handle ? "overlay" : h == _textForm.Handle ? "textbox" : "other";
        lock (_result)
        {
            _result.ForegroundSamples++;
            if (who == "overlay") _result.OverlayForegroundCount++;
        }
        if (who == "overlay") AppLog.Write("overlay-took-foreground");
        return who;
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '+' ? '_' : c));

    private void OnFinished(UtteranceReport r)
    {
        AppLog.Write(AppLog.Describe(r));
        lock (_result)
        {
            if (r.Outcome is Outcome.Cancelled or Outcome.Failed)
                _result.Cancellations.Add(new CancelRecord { Seq = r.Seq, Reason = r.Reason.ToString(), HeldMs = r.HeldMs, Error = r.Error });
            else
                _result.Deliveries.Add(new DeliveryRecord
                {
                    Seq = r.Seq, To = r.Outcome == Outcome.Pasted ? "textbox" : "clipboard", Text = r.Text ?? "",
                    MicOpenMs = r.MicOpenMs, HeldMs = r.HeldMs, ReleaseToDeliverMs = r.ReleaseToDeliverMs, TranscribeMs = r.TranscribeMs,
                });
        }
    }

    private async Task RunAsync()
    {
        var timeout = Task.Delay(_sc.TimeoutMs);
        _ = LoadEngineAsync();
        var run = Task.Run(ExecuteActionsAsync);
        var first = await Task.WhenAny(run, timeout);
        if (first == timeout) _result.Error = $"台本が {_sc.TimeoutMs} ms で終わらない";
        else if (run.Exception is { } ex) _result.Error = ex.InnerException?.Message ?? ex.Message;
        else _result.Completed = true;
        await Task.Delay(300);
        _overlay.Invoke(() =>
        {
            _fgProbe.Stop();
            _result.Textbox = _textForm.Box.Text;
            _result.Clipboard = _clip.Text;
            _result.Runtime = _engine.Runtime;
            lock (_result) _result.Save(Path.Combine(_out, "result.json"));
        });
        AppLog.Write($"done completed={_result.Completed} error={_result.Error}");
        await _ptt.DisposeAsync();
        _overlay.BeginInvoke(ExitThread);
    }

    private async Task LoadEngineAsync()
    {
        try
        {
            _ptt.SetModelStatus(false, null);
            var dir = _sc.ModelsDir is null ? ModelCatalog.DefaultModelsDirectory : Scenario.ResolvePath(_scenarioPath, _sc.ModelsDir);
            var t = await EngineLoader.LoadAsync(_settings, dir, p => _ptt.SetModelStatus(false, p), CancellationToken.None);
            _engine.Set(t);
            _result.ModelReadyMs = _sw.ElapsedMilliseconds;
            _result.Runtime = t.Runtime;
            _result.Model = ModelCatalog.Get(_settings.Model).Name;
            AppLog.Write($"engine-ready model={_result.Model} runtime={t.Runtime} ms={_sw.ElapsedMilliseconds}");
            _ptt.SetModelStatus(true);
            _modelReady.TrySetResult();
        }
        catch (Exception ex)
        {
            AppLog.Write("engine-error " + ex.Message);
            _modelReady.TrySetException(ex);
        }
    }

    private async Task ExecuteActionsAsync()
    {
        foreach (var a in _sc.Actions)
        {
            AppLog.Write($"action {a.Do}");
            switch (a.Do)
            {
                case "waitModel":
                    var w = await Task.WhenAny(_modelReady.Task, Task.Delay(a.TimeoutMs ?? 600_000));
                    if (w != _modelReady.Task) throw new TimeoutException("モデルの用意が終わらない");
                    await _modelReady.Task;
                    break;
                case "press":
                    _recorder.NextAudio = a.Audio is null ? [] : Audio.ReadWav16kMono(Scenario.ResolvePath(_scenarioPath, a.Audio));
                    Key(a.Key, down: true);
                    break;
                case "holdUntilAudioEnd":
                    await _recorder.WaitAudioEndAsync();
                    break;
                case "release":
                    Key(a.Key, down: false);
                    break;
                case "key":
                    Key(a.Key ?? "C", down: true);
                    Key(a.Key ?? "C", down: false);
                    break;
                case "focus":
                    _fg.Focus(a.Window ?? "textbox");
                    break;
                case "lockClipboard":
                    _clip.LockFor(a.Ms ?? 0);
                    break;
                case "wait":
                    await Task.Delay(a.Ms ?? 0);
                    break;
                case "waitIdle":
                    if (!await _ptt.WaitIdleAsync(TimeSpan.FromMilliseconds(a.TimeoutMs ?? 300_000)))
                        throw new TimeoutException("処理待ちが 0 にならない");
                    await Task.Delay(200);
                    break;
                default:
                    throw new InvalidDataException($"未知の手: {a.Do}");
            }
        }
    }
}

/// <summary>検証用のテキスト欄(貼り付け先ウィンドウの代わり)。</summary>
internal sealed class VerifyTextForm : Form
{
    public TextBox Box { get; } = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 11f) };

    public VerifyTextForm()
    {
        Text = "Voice2Txt 検証用のテキスト欄";
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(40, 40, 520, 240);
        ShowInTaskbar = false;
        Controls.Add(Box);
    }

    protected override bool ShowWithoutActivation => true;
}

/// <summary>マイクの代わり。押下時に台本の音声を受け取り、開くまでの遅れの後に録音中にする。離した時点までの音声を返す。</summary>
internal sealed class VerifyRecorder : IRecorder
{
    public const int MicOpenDelayMs = 150;
    public float[] NextAudio { get; set; } = [];
    private TaskCompletionSource _audioEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAudioEndAsync() => _audioEnd.Task;

    public IRecording Start(Action onStarted)
    {
        var audio = NextAudio;
        _audioEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var end = _audioEnd;
        var r = new Rec(audio);
        _ = Task.Run(async () =>
        {
            await Task.Delay(MicOpenDelayMs);
            r.MarkStarted();
            onStarted();
            await Task.Delay((int)(audio.Length * 1000L / Audio.SampleRate));
            end.TrySetResult();
        });
        return r;
    }

    private sealed class Rec(float[] audio) : IRecording
    {
        private Stopwatch? _since;
        public void MarkStarted() => _since = Stopwatch.StartNew();

        public Task<float[]> StopAsync()
        {
            if (_since is null) return Task.FromResult(Array.Empty<float>());
            long n = Math.Min(audio.Length, _since.ElapsedMilliseconds * Audio.SampleRate / 1000);
            return Task.FromResult(audio[..(int)n]);
        }

        public void Abort() { }
    }
}

internal sealed class VirtualForeground(nint textbox) : IForegroundWindow
{
    private volatile string _current = "textbox";
    public void Focus(string window) => _current = window;
    public nint Current() => _current == "textbox" ? textbox : 1;
}

/// <summary>検証用のクリップボード。台本の lockClipboard の間は、他アプリが開いたままのように投げる。</summary>
internal sealed class MemoryClipboard : IClipboard
{
    private long _lockedUntil;
    public string? Text { get; private set; }

    public void LockFor(int ms) => Interlocked.Exchange(ref _lockedUntil, Environment.TickCount64 + ms);

    public void SetText(string text)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _lockedUntil))
            throw new System.Runtime.InteropServices.ExternalException("クリップボードを開けません(検証モードの lockClipboard)");
        Text = text;
    }
}

/// <summary>Ctrl+V の代わりに、クリップボードの中身を検証用のテキスト欄のカーソル位置へ貼る。</summary>
internal sealed class TextBoxPaster(VerifyTextForm form, MemoryClipboard clip) : IPasteSender
{
    public void SendPaste() => form.Invoke(() => form.Box.SelectedText = clip.Text ?? "");
}
