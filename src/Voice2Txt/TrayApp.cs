using System.Collections.Concurrent;
using System.Diagnostics;
using Voice2Txt.Core;

namespace Voice2Txt;

internal static class TrayIcons
{
    public static Icon Make(Color c)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 1, 1, 14, 14);
            using var w = new SolidBrush(Color.White);
            g.FillRectangle(w, 6, 3, 4, 7);
            g.FillRectangle(w, 7, 10, 2, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static readonly Icon Idle = Make(Color.FromArgb(0x99, 0xAA, 0xAA));
    public static readonly Icon Rec = Make(Color.FromArgb(0xE5, 0x38, 0x3B));
    public static readonly Icon Busy = Make(Color.FromArgb(0xF4, 0xA2, 0x59));

    public static Icon For(OverlayState s) => s switch
    {
        OverlayState.Recording or OverlayState.ProcessingAndRecording => Rec,
        OverlayState.Preparing or OverlayState.Processing or OverlayState.ModelPreparing => Busy,
        _ => Idle,
    };
}

/// <summary>タスクトレイ常駐。起動時にモデルを用意して読み込み、トークキーで録音・文字起こし・貼り付けを行う。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly OverlayForm _overlay;
    private readonly KeyboardHook _hook;
    private readonly PushToTalkController _ptt;
    private readonly DeferredTranscriber _engine = new();
    private readonly BlockingCollection<Action> _keys = new();
    private readonly CancellationTokenSource _cts = new();
    private string _modelStatus = "モデル準備中";
    private readonly ToolStripMenuItem _autoItem;
    private readonly string _exe;
    private readonly IAutoStartRegistry _autoStartReg;
    private SettingsForm? _settingsForm;

    public TrayApp(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        var talk = TalkKeys.Parse(settings.TalkKey);
        _overlay = new OverlayForm(TalkKeys.DisplayName(talk), () => _ptt?.InputLevel ?? 0);
        _ = _overlay.Handle; // UI スレッドで作る(クリップボードの Invoke 先)
        _tray = new NotifyIcon { Icon = TrayIcons.Busy, Visible = true, Text = AppVersion.TrayText("モデル準備中") };
        var menu = new ContextMenuStrip();
        // 先頭に版(タグ)。実機受け入れで、今動いている版を画面から確かめられるように
        menu.Items.Add(new ToolStripMenuItem(AppVersion.Label) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("設定…", null, (_, _) => OpenSettings(settingsPath));
        menu.Items.Add("設定ファイルを開く", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{settingsPath}\"") { UseShellExecute = true }));
        menu.Items.Add("設定フォルダを開く", null, (_, _) => Process.Start(new ProcessStartInfo(AppSettings.DefaultDirectory) { UseShellExecute = true }));
        // ログオン時の自動起動(初期値オフ)。起動時に設定と登録を一致させ、メニューで切り替える
        var autoStart = new RunKeyAutoStart();
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        try { AutoStart.Sync(settings.AutoStart, exe, autoStart); } catch (Exception ex) { AppLog.Write("autostart-error " + ex.Message); }
        var autoItem = _autoItem = new ToolStripMenuItem("ログオン時に起動する") { Checked = settings.AutoStart };
        _exe = exe; _autoStartReg = autoStart;
        autoItem.Click += (_, _) =>
        {
            try
            {
                autoItem.Checked = AutoStart.Toggle(_settings, settingsPath, exe, autoStart);
                AppLog.Write($"autostart={autoItem.Checked}");
            }
            catch (Exception ex) { AppLog.Write("autostart-error " + ex.Message); }
        };
        menu.Items.Add(autoItem);
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;

        _ptt = new PushToTalkController(
            new PttDependencies(new WaveInRecorder(), new Win32Foreground(), new Win32Clipboard(_overlay), new CtrlVSender(), _engine, new SystemClock()),
            PttOptions.From(settings));
        _ptt.OverlayChanged += v => _overlay.BeginInvoke(() =>
        {
            _overlay.Apply(v);
            _tray.Icon = TrayIcons.For(v.State);
        });
        _ptt.Finished += r => AppLog.Write(AppLog.Describe(r));

        // フックのコールバックは軽く保つ: キー操作は専用スレッドで順に処理する
        new Thread(() => { foreach (var a in _keys.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { AppLog.Write("key-error " + ex.Message); } } })
        { IsBackground = true, Name = "talk-key" }.Start();
        _hook = new KeyboardHook((ushort)talk);
        _hook.TalkDown += () => _keys.Add(_ptt.OnTalkKeyDown);
        _hook.TalkUp += () => _keys.Add(_ptt.OnTalkKeyUp);
        _hook.OtherKeyWhileTalk += () => _keys.Add(_ptt.OnOtherKeyDown);

        if (TalkKeys.InvalidWarning(settings.TalkKey) is { } warn)
        {
            AppLog.Write($"talkkey-invalid name={settings.TalkKey} fallback={TalkKeys.Default}");
            _tray.ShowBalloonTip(8000, "Voice2Txt", warn, ToolTipIcon.Warning);
        }

        _ = LoadEngineAsync();
    }

    private async Task LoadEngineAsync()
    {
        try
        {
            _ptt.SetModelStatus(false, null);
            var sw = Stopwatch.StartNew();
            void Progress(string p)
            {
                _modelStatus = $"モデル準備中 {p}";
                _ptt.SetModelStatus(false, p);
                _overlay.BeginInvoke(() => _tray.Text = AppVersion.TrayText(_modelStatus));
            }
            // CUDA の実行時ライブラリは配布物に入れない: 無ければ初回に NVIDIA の redist から取得して runtime フォルダに置き、そこから読む
            var cudaNote = await CudaRuntimeLoader.PrepareAsync(
                new CudaRuntimeProvisioner(CudaRuntimeCatalog.DefaultDirectory), _settings.FetchCudaRuntime, Progress, _cts.Token);
            if (cudaNote is not null)
                _overlay.BeginInvoke(() => _tray.ShowBalloonTip(5000, "Voice2Txt", cudaNote, ToolTipIcon.Info));
            var t = await EngineLoader.LoadAsync(_settings, ModelCatalog.DefaultModelsDirectory, Progress, _cts.Token);
            _engine.Set(t);
            _ptt.SetModelStatus(true);
            var model = ModelCatalog.Get(_settings.Model).Name;
            AppLog.Write($"engine-ready model={model} runtime={t.Runtime} ms={sw.ElapsedMilliseconds}");
            var cpu = t.Runtime.Equals("Cuda", StringComparison.OrdinalIgnoreCase) ? "" : "、CUDA 無し(CPU)";
            _overlay.BeginInvoke(() => { _tray.Text = AppVersion.TrayText($"待機中(モデル {model}{cpu})"); _tray.Icon = TrayIcons.Idle; });
        }
        catch (Exception ex)
        {
            AppLog.Write("engine-error " + ex.Message);
            _overlay.BeginInvoke(() =>
            {
                _tray.Text = AppVersion.TrayText("モデルを用意できません");
                _tray.ShowBalloonTip(5000, "Voice2Txt", "モデルを用意できませんでした: " + ex.Message, ToolTipIcon.Error);
            });
        }
    }

    /// <summary>トレイの「設定…」: 設定画面を開く(開いていれば前に出す)。保存したら自動起動の登録を合わせ、再起動で効く項目が変われば再起動を勧める。</summary>
    private void OpenSettings(string settingsPath)
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        var form = _settingsForm = new SettingsForm(_settings, settingsPath);
        form.Saved += (_, next, changed) =>
        {
            _settings.CopyFrom(next); // トレイの自動起動の切り替えが、保存した値を古い値で上書きしないように
            try { AutoStart.Sync(_settings.AutoStart, _exe, _autoStartReg); _autoItem.Checked = _settings.AutoStart; }
            catch (Exception ex) { AppLog.Write("autostart-error " + ex.Message); }
            if (changed.Any(k => k != "autoStart")
                && MessageBox.Show("設定を保存しました。トークキー・モデルなどの変更は再起動で効きます。今すぐ再起動しますか?",
                    "Voice2Txt", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Program.RestartRequested = true;
                AppLog.Write("restart-requested");
                ExitThread();
            }
        };
        form.FormClosed += (_, _) => _settingsForm = null;
        form.Show();
        form.Activate();
    }

    public void NotifyAlreadyRunning() =>
        _overlay.BeginInvoke(() => _tray.ShowBalloonTip(3000, "Voice2Txt", "Voice2Txt は既に起動しています", ToolTipIcon.Info));

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _keys.CompleteAdding();
        _cts.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _ptt.DisposeAsync().AsTask().Wait(2000);
        _engine.Dispose();
        _overlay.Close();
        base.ExitThreadCore();
    }
}

/// <summary>モデルの読み込みが終わるまでの仮の受け口(読み込み後に本物へ委ねる)。</summary>
internal sealed class DeferredTranscriber : ITranscriber, IDisposable
{
    private WhisperTranscriber? _inner;
    public string? Runtime => _inner?.Runtime;
    /// <summary>読み込んだエンジンに委ねる(読み込み直したときは前のものを片付ける)。</summary>
    public void Set(WhisperTranscriber t)
    {
        var old = _inner;
        _inner = t;
        if (old is not null && !ReferenceEquals(old, t)) old.Dispose();
    }

    public Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct) =>
        _inner?.TranscribeAsync(samples16k, partial, ct) ?? throw new InvalidOperationException("モデルが未準備");

    public Task<string> TranscribeInterimAsync(float[] samples16k, CancellationToken ct) =>
        _inner?.TranscribeInterimAsync(samples16k, ct) ?? throw new InvalidOperationException("モデルが未準備");

    public void Dispose() => _inner?.Dispose();
}

internal static class TalkKeys
{
    public const Keys Default = Keys.RControlKey;

    /// <summary>単独のキーの名前(Keys の名前。修飾の組み合わせ・未定義の値は読めない扱い)。</summary>
    public static bool TryParse(string? name, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(name) || name.Contains(',') || name.Contains('+')) return false;
        if (!Enum.TryParse(name.Trim(), ignoreCase: true, out Keys k) || k == Keys.None || (k & Keys.Modifiers) != 0 || !Enum.IsDefined(k)) return false;
        key = k;
        return true;
    }

    /// <summary>読めなければ初期値(右 Ctrl)。黙って戻さないよう、呼び出し側は <see cref="InvalidWarning"/> を知らせる。</summary>
    public static Keys Parse(string? name) => TryParse(name, out var k) ? k : Default;

    /// <summary>talkKey を読めないときの知らせ(読めれば null)。</summary>
    public static string? InvalidWarning(string? name) =>
        TryParse(name, out _) ? null : $"設定の talkKey「{name}」を読めないため {DisplayName(Default)} を使います";

    /// <summary>画面に出す名前(設定画面の選択肢と同じ。<see cref="SettingsSchema.TalkKeyChoices"/>)。</summary>
    public static string DisplayName(Keys k) => SettingsSchema.TalkKeyLabel(k.ToString());
}
