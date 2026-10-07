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
/// 設定画面は台本の openSettings / setSetting / saveSettings で本物の部品を操作し、&lt;out&gt;/settings.json に保存する。
/// restart はアプリを起動し直したのと同じく、その settings.json を読み直してトークキー・設定(モデルが変われば読み込みも)をやり直す。
/// </summary>
internal sealed class VerifyHost : ApplicationContext
{
    private readonly Scenario _sc;
    private readonly string _scenarioPath, _out, _shots, _settingsPath;
    private AppSettings _settings;
    private Keys _talkKey;
    private TalkKeyFilter _keys;
    private readonly VerifyTextForm _textForm;
    private readonly OverlayForm _overlay;
    private readonly VerifyRecorder _recorder = new();
    private readonly VirtualForeground _fg;
    private readonly MemoryClipboard _clip = new();
    private readonly TextBoxPaster _paster;
    private PushToTalkController _ptt;
    private readonly DeferredTranscriber _engine = new();
    private readonly VerifyResult _result;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TaskCompletionSource _modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SettingsForm? _settingsForm;
    private int _shotNo;
    private readonly System.Windows.Forms.Timer _fgProbe = new() { Interval = 25 };
    private readonly ModelPrepTracker _prepTrack = new();
    private OverlayState _lastRecorded = OverlayState.Hidden;
    private string? _lastPrepMilestone;
    private string? _fetchDir; // modelPrep = download で取得したモデルを置くフォルダ(終わったら消す)

    public VerifyHost(string scenarioPath, string outDir)
    {
        _scenarioPath = scenarioPath;
        _sc = Scenario.Load(scenarioPath);
        _out = Path.GetFullPath(outDir);
        _shots = Path.Combine(_out, "shots");
        _settingsPath = Path.Combine(_out, "settings.json");
        Directory.CreateDirectory(_shots);
        AppLog.Path = Path.Combine(_out, "verify.log");
        _result = new VerifyResult { Scenario = _sc.Name, Version = AppVersion.Tag(AppVersion.Current), TrayTooltip = AppVersion.TrayText("モデル準備中") };
        _settings = _sc.Settings ?? new AppSettings();
        (_talkKey, _keys) = TalkKeyOf(_settings);

        _textForm = new VerifyTextForm();
        _textForm.Show();
        _overlay = new OverlayForm(TalkKeys.DisplayName(_talkKey), () => _ptt?.InputLevel ?? 0);
        _ = _overlay.Handle;
        _fg = new VirtualForeground(_textForm.Handle);

        _paster = new TextBoxPaster(_textForm, _clip);
        _ptt = CreatePtt();
        _fgProbe.Tick += (_, _) => ProbeForeground();
        _fgProbe.Start();

        _ = RunAsync();
    }

    /// <summary>設定のトークキーと、それを判定するフィルタ。読めなければ右 Ctrl にし、警告を結果に残す。</summary>
    private (Keys, TalkKeyFilter) TalkKeyOf(AppSettings s)
    {
        var key = TalkKeys.Parse(s.TalkKey);
        if (TalkKeys.InvalidWarning(s.TalkKey) is { } warn)
        {
            lock (_result) _result.Warnings.Add(warn);
            AppLog.Write($"talkkey-invalid name={s.TalkKey} fallback={TalkKeys.Default}");
        }
        return (key, new TalkKeyFilter((int)key));
    }

    private PushToTalkController CreatePtt()
    {
        var p = new PushToTalkController(
            new PttDependencies(_recorder, _fg, _clip, _paster, _engine, new SystemClock()),
            PttOptions.From(_settings));
        p.OverlayChanged += v => _overlay.BeginInvoke(() => OnOverlay(v));
        p.Finished += OnFinished;
        return p;
    }

    /// <summary>台本の restart: 保存した settings.json を読み直し、アプリを起動し直したのと同じ設定で受け付け直す。</summary>
    private async Task RestartAsync()
    {
        await _ptt.WaitIdleAsync(TimeSpan.FromSeconds(30));
        var old = _ptt;
        var next = AppSettings.LoadOrCreate(_settingsPath);
        bool modelChanged = !string.Equals(ModelCatalog.TryGet(next.Model, out var a) ? a.Name : next.Model,
                                           ModelCatalog.TryGet(_settings.Model, out var b) ? b.Name : _settings.Model, StringComparison.OrdinalIgnoreCase);
        _settings = next;
        (_talkKey, _keys) = TalkKeyOf(next);
        _overlay.Invoke(() => _overlay.SetTalkKeyName(TalkKeys.DisplayName(_talkKey)));
        _ptt = CreatePtt();
        _ptt.InterimEnabled = Backends.InterimAllowed(_engine.Runtime, true);
        await old.DisposeAsync();
        lock (_result) _result.Restarts++;
        AppLog.Write($"restart talkKey={_talkKey} model={next.Model} modelChanged={modelChanged}");
        if (modelChanged)
        {
            _modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = LoadEngineAsync();
        }
        else _ptt.SetModelStatus(_modelReady.Task.IsCompletedSuccessfully);
    }

    /// <summary>設定画面を撮る(自前で描く)。結果の shots に「設定画面」の状態で残す。</summary>
    private void SettingsShot(string name)
    {
        var file = $"{++_shotNo:00}-設定画面-{Safe(name)}.png";
        _settingsForm!.SaveScreenshot(Path.Combine(_shots, file));
        lock (_result) _result.Shots.Add(new ShotRecord { Name = name, AtMs = _sw.ElapsedMilliseconds, State = "設定画面", Screenshot = "shots/" + file, Capture = "render" });
        AppLog.Write($"shot {name} state=設定画面");
    }

    private void OnOverlay(OverlayView v)
    {
        _overlay.Apply(v);
        // モデル準備中は進み具合が 1% ごとに変わる: 記録とスクショは段階が変わるか 10% の刻みを跨いだときだけ
        if (v.State == OverlayState.ModelPreparing)
        {
            var k = ModelPrepProgress.Milestone(v.Detail);
            if (_lastRecorded == OverlayState.ModelPreparing && k == _lastPrepMilestone) return;
            _lastPrepMilestone = k;
        }
        _lastRecorded = v.State;
        var rec = new StateRecord
        {
            AtMs = _sw.ElapsedMilliseconds, State = v.Label, Text = v.State == OverlayState.Hidden ? "" : v.Text(TalkKeys.DisplayName(_talkKey)),
            InterimChars = v.Interim?.Length ?? 0,
        };
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
        AppLog.Write($"state {v.Label} interim={rec.InterimChars} capture={rec.Capture}");
    }

    /// <summary>台本の shot: 今のオーバーレイを撮り、そのとき描いた音量バーの値と一緒に結果に残す(UI スレッド)。</summary>
    private void Shot(string? name)
    {
        name ??= "shot";
        var v = _overlay.View;
        double meter = _overlay.SampleMeter();
        _overlay.Refresh();
        Thread.Sleep(40);
        var file = $"{++_shotNo:00}-{Safe(v.Label)}-{Safe(name)}.png";
        var rec = new ShotRecord
        {
            Name = name, AtMs = _sw.ElapsedMilliseconds, State = v.Label, Meter = Math.Round(meter, 3),
            InterimChars = _overlay.InterimShown?.Length ?? 0, InterimLines = _overlay.InterimLines,
            HintRowX = _overlay.HintRowOnScreen.X, HintRowY = _overlay.HintRowOnScreen.Y, Screenshot = "shots/" + file,
        };
        rec.Capture = _overlay.SaveScreenshot(Path.Combine(_shots, file));
        lock (_result) _result.Shots.Add(rec);
        AppLog.Write($"shot {name} state={v.Label} meter={rec.Meter} interim={rec.InterimChars} lines={rec.InterimLines} row=({rec.HintRowX},{rec.HintRowY}) capture={rec.Capture}");
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
                    FillersRemoved = r.FillersRemoved,
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
        await Task.WhenAny(_paster.DrainAsync(), Task.Delay(10_000));
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
        if (_fetchDir is not null)
            try { Directory.Delete(_fetchDir, true); }
            catch (Exception ex) { AppLog.Write("model-prep cleanup-error " + ex.Message); }
        _overlay.BeginInvoke(ExitThread);
    }

    private async Task LoadEngineAsync()
    {
        try
        {
            _prepTrack.Reset();
            _ptt.SetModelStatus(false, null);
            var dir = _sc.ModelsDir is null ? ModelCatalog.DefaultModelsDirectory : Scenario.ResolvePath(_scenarioPath, _sc.ModelsDir);
            if (Backends.InvalidWarning(_settings.Backend) is { } bw)
            {
                lock (_result) _result.Warnings.Add(bw);
                AppLog.Write($"backend-invalid value={_settings.Backend} fallback={Backends.Auto}");
            }
            // 検証モードは CUDA の実行時ライブラリを取得しない(取得済みの runtime フォルダがあればそれを読み、無ければ次のバックエンド)。
            // CUDA を試さない設定(vulkan / cpu)なら読みもしない(常駐時と同じ)
            var cudaNote = Backends.NeedsCudaRuntime(_settings.Backend)
                ? await CudaRuntimeLoader.PrepareAsync(
                    new CudaRuntimeProvisioner(CudaRuntimeCatalog.DefaultDirectory), fetch: false, ModelProgress, CancellationToken.None)
                : "CUDA を試さない設定です";
            lock (_result) { _result.CudaRuntime = cudaNote ?? "ready"; _result.Backend = _settings.Backend; }
            ModelProvisioner? prov = null;
            switch (_sc.ModelPrep)
            {
                case null: break;
                case "download":
                    // 手元のモデルを取得元にして、空のフォルダへ取得から始める(手元にも無ければ先に本物の取得元から手元へ)
                    await new ModelProvisioner(dir).EnsureAsync(ModelCatalog.Get(_settings.Model), null, CancellationToken.None);
                    var src = dir;
                    dir = _fetchDir = Path.Combine(_out, "models");
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    prov = new ModelProvisioner(dir, new HttpClient(new LocalModelSourceHandler(src, _sc.ModelFetchMs)) { Timeout = Timeout.InfiniteTimeSpan });
                    AppLog.Write($"model-prep source={src} fetchMs={_sc.ModelFetchMs}");
                    break;
                default: throw new InvalidDataException($"modelPrep を読めない: {_sc.ModelPrep}(download のみ)");
            }
            var t = await EngineLoader.LoadAsync(_settings, dir, ModelProgress, CancellationToken.None, prov);
            _engine.Set(t);
            _result.ModelReadyMs = _sw.ElapsedMilliseconds;
            _result.Runtime = t.Runtime;
            _result.Model = ModelCatalog.Get(_settings.Model).Name;
            lock (_result) _result.TrayTooltip = AppVersion.TrayText(Backends.IdleTrayStatus(_result.Model, t.Runtime)); // 常駐時と同じ文言
            _ptt.InterimEnabled = Backends.InterimAllowed(t.Runtime, true); // CPU では途中経過を出さない(常駐時と同じ)
            lock (_result) _result.Interim = _ptt.InterimEnabled && _settings.ShowInterim;
            AppLog.Write($"engine-ready model={_result.Model} runtime={t.Runtime} ms={_sw.ElapsedMilliseconds} backend={_settings.Backend} interim={_result.Interim}");
            _ptt.SetModelStatus(true);
            _modelReady.TrySetResult();
        }
        catch (Exception ex)
        {
            AppLog.Write("engine-error " + ex.Message);
            _modelReady.TrySetException(ex);
        }
    }

    /// <summary>
    /// モデルの準備の進み具合(常駐時の TrayApp の Progress と同じ文)。オーバーレイへ渡し、段階が変わるか 10% の刻みを跨いだら
    /// そのときのトレイのツールチップと一緒に result.json の modelPrep と verify.log に残す。
    /// </summary>
    private void ModelProgress(string p)
    {
        _ptt.SetModelStatus(false, p);
        if (!_prepTrack.Accept(p)) return;
        var (stage, pct) = ModelPrepProgress.Parse(p);
        var tip = AppVersion.TrayText(ModelPrepProgress.TrayStatus(p));
        long at = _sw.ElapsedMilliseconds;
        lock (_result)
        {
            _result.ModelPrep.Add(new ModelPrepRecord { AtMs = at, Stage = stage, Percent = pct, Tooltip = tip });
            _result.TrayTooltip = tip;
        }
        AppLog.Write($"model-prep stage={stage}{(pct is { } x ? $" pct={x}" : "")} atMs={at}");
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
                case "pasteReadDelay":
                    _paster.ReadDelayMs = a.Ms ?? 0;
                    break;
                case "wait":
                    await Task.Delay(a.Ms ?? 0);
                    break;
                case "shot":
                    _overlay.Invoke(() => Shot(a.Name));
                    break;
                case "waitIdle":
                    if (!await _ptt.WaitIdleAsync(TimeSpan.FromMilliseconds(a.TimeoutMs ?? 300_000)))
                        throw new TimeoutException("処理待ちが 0 にならない");
                    await Task.Delay(200);
                    break;
                case "openSettings":
                    _overlay.Invoke(() =>
                    {
                        _settings.Save(_settingsPath); // 動いている設定がファイルにある状態から開く(本番と同じ)
                        _settingsForm?.Close();
                        _settingsForm = new SettingsForm(_settings, _settingsPath) { StartPosition = FormStartPosition.Manual, Location = new Point(600, 40) };
                        _settingsForm.Saved += (_, next, changed) =>
                        {
                            lock (_result) { _result.SavedSettings = next; _result.SettingsChanged = changed; }
                        };
                        _settingsForm.Show();
                        _settingsForm.Refresh();
                        SettingsShot(a.Name ?? "開いた");
                    });
                    break;
                case "setSetting":
                    _overlay.Invoke(() => (_settingsForm ?? throw new InvalidOperationException("設定画面が開いていない(openSettings が先)"))
                        .SetValue(a.Name ?? throw new InvalidDataException("setSetting に name(項目のキー)が無い"), a.Value ?? ""));
                    break;
                case "saveSettings":
                    _overlay.Invoke(() =>
                    {
                        var form = _settingsForm ?? throw new InvalidOperationException("設定画面が開いていない(openSettings が先)");
                        form.Refresh();
                        SettingsShot(a.Name ?? "保存前");
                        if (form.ClickSave() is { } err)
                        {
                            lock (_result) _result.SettingsErrors.Add(err);
                            AppLog.Write("settings-error");
                        }
                    });
                    break;
                case "restart":
                    await RestartAsync();
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

        /// <summary>流し込んでいる音声の、今の位置の直前 0.1 秒の RMS(流し終えた後は 0)。</summary>
        public double InputRms
        {
            get
            {
                if (_since is null) return 0;
                long n = _since.ElapsedMilliseconds * Audio.SampleRate / 1000;
                return n >= audio.Length ? 0 : Audio.TailRms(audio, (int)n);
            }
        }

        /// <summary>今の位置までに流した音声(押下中の途中経過に使う)。</summary>
        public float[] Snapshot()
        {
            if (_since is null) return [];
            long n = Math.Min(audio.Length, _since.ElapsedMilliseconds * Audio.SampleRate / 1000);
            return audio[..(int)n];
        }

        public Task<float[]> StopAsync() => Task.FromResult(Snapshot());

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

/// <summary>
/// Ctrl+V の代わりに、クリップボードの中身を検証用のテキスト欄のカーソル位置へ貼る。
/// 台本の pasteReadDelay を指定すると、本物の貼り付け先のように Ctrl+V を受けてから ms 後にクリップボードを読む(遅い貼り付け先)。
/// </summary>
internal sealed class TextBoxPaster(VerifyTextForm form, MemoryClipboard clip) : IPasteSender
{
    private readonly List<Task> _reads = [];
    public volatile int ReadDelayMs;

    public void SendPaste()
    {
        int delay = ReadDelayMs;
        if (delay <= 0) { form.Invoke(() => form.Box.SelectedText = clip.Text ?? ""); return; }
        var t = Task.Run(async () =>
        {
            await Task.Delay(delay);
            form.Invoke(() => form.Box.SelectedText = clip.Text ?? "");
        });
        lock (_reads) _reads.Add(t);
    }

    /// <summary>遅れて読む貼り付けが全部終わるまで待つ。</summary>
    public Task DrainAsync() { lock (_reads) return Task.WhenAll(_reads.ToArray()); }
}
