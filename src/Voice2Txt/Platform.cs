using Voice2Txt.Core;
using NAudio.Wave;

namespace Voice2Txt;

/// <summary>Windows 既定の録音デバイス。押した瞬間に開き、最初の音が届いたら録音中。</summary>
internal sealed class WaveInRecorder : IRecorder
{
    public IRecording Start(Action onStarted) => new Rec(onStarted);

    private sealed class Rec : IRecording
    {
        private readonly WaveIn _w = new() { WaveFormat = new WaveFormat(Audio.SampleRate, 16, 1), BufferMilliseconds = 50 };
        private readonly List<float> _buf = [];
        private readonly TaskCompletionSource<float[]> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private double _rms;

        public Rec(Action onStarted)
        {
            _w.DataAvailable += (_, e) =>
            {
                lock (_buf)
                {
                    if (!_started) { _started = true; onStarted(); }
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2) _buf.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                    _rms = Audio.TailRms(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf), _buf.Count);
                }
            };
            _w.RecordingStopped += (_, e) =>
            {
                lock (_buf) _done.TrySetResult(_buf.ToArray());
                _w.Dispose();
            };
            _w.StartRecording();
        }

        public Task<float[]> StopAsync() { _w.StopRecording(); return _done.Task; }
        public void Abort() { try { _w.StopRecording(); } catch { } }
        public double InputRms { get { lock (_buf) return _rms; } }
        public float[] Snapshot() { lock (_buf) return _buf.ToArray(); }
    }
}

/// <param name="ui">前面を移す処理を走らせる UI スレッド(入力の待ち行列を持つスレッド)。</param>
internal sealed class Win32Foreground(Control ui) : IForegroundWindow
{
    public nint Current() => Native.GetForegroundWindow();

    /// <summary>
    /// 届け方 return: そのウィンドウを前面にする。背後のアプリは前面を奪えないので、今の前面と相手のスレッドの入力に一時的につないでから移す。
    /// 移ったかは前面を確かめて返す(移れなければ呼び出し側が退避する)。
    /// </summary>
    public bool Activate(nint window) => ui.Invoke(() =>
    {
        if (!Native.IsWindow(window)) return false;
        nint fg = Native.GetForegroundWindow();
        if (fg == window) return true;
        uint me = Native.GetCurrentThreadId();
        uint fgThread = fg == 0 ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        uint toThread = Native.GetWindowThreadProcessId(window, out _);
        bool a1 = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        bool a2 = toThread != 0 && toThread != me && toThread != fgThread && Native.AttachThreadInput(me, toThread, true);
        try
        {
            if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
            Native.BringWindowToTop(window);
            Native.SetForegroundWindow(window);
        }
        finally
        {
            if (a2) Native.AttachThreadInput(me, toThread, false);
            if (a1) Native.AttachThreadInput(me, fgThread, false);
        }
        for (int i = 0; i < 20 && Native.GetForegroundWindow() != window; i++) Thread.Sleep(10);
        bool ok = Native.GetForegroundWindow() == window;
        AppLog.Write($"activate ok={ok}");
        return ok;
    });
}

/// <summary>
/// クリップボードは STA の UI スレッドで触る。既定の届け方では元の内容には戻さない(Win+V の履歴で取り出す運用)。届け方 return では貼った後に戻す。
/// 1 回の呼び出しでは短く試すだけにし(UI を止めない)、入らなければ投げる。粘るのは PushToTalkController。
/// </summary>
internal sealed class Win32Clipboard(Control ui) : IClipboard
{
    private uint _afterSet; // 最後に確定版を入れた直後のクリップボードの通し番号

    public void SetText(string text) => ui.Invoke(() =>
    {
        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, retryTimes: 3, retryDelay: 20);
        _afterSet = Native.GetClipboardSequenceNumber();
    });

    /// <summary>届け方 return: 今の中身を形式ごとに写す(読めない形式は写さない)。空なら空の写し。</summary>
    public object? Save() => ui.Invoke(() =>
    {
        var copy = new DataObject();
        var src = Clipboard.GetDataObject();
        if (src is null) return (object)copy;
        foreach (var f in src.GetFormats(autoConvert: false))
        {
            try { if (src.GetData(f, autoConvert: false) is { } d) copy.SetData(f, d); }
            catch (Exception) { } // 遅延描画などで読めない形式は写さない
        }
        return copy;
    });

    /// <summary>確定版を入れた後に誰も書き換えていなければ、写しへ戻す(利用者がその間にコピーしたものは消さない)。</summary>
    public bool Restore(object saved) => ui.Invoke(() =>
    {
        if (saved is not DataObject d || Native.GetClipboardSequenceNumber() != _afterSet) return false;
        if (d.GetFormats().Length == 0) Clipboard.Clear();
        else Clipboard.SetDataObject(d, copy: true, retryTimes: 3, retryDelay: 20);
        AppLog.Write($"clipboard-restored formats={d.GetFormats().Length}");
        return true;
    });
}

internal sealed class CtrlVSender : IPasteSender
{
    public void SendPaste()
    {
        Thread.Sleep(30);
        const ushort CTRL = 0xA2, V = 0x56;
        Native.SendKeys(Native.Key(CTRL, false), Native.Key(V, false), Native.Key(V, true), Native.Key(CTRL, true));
    }
}

/// <summary>ログオン時の自動起動: HKCU\Software\Microsoft\Windows\CurrentVersion\Run の値 Voice2Txt。</summary>
internal sealed class RunKeyAutoStart(string name = AppVersion.ProductName) : IAutoStartRegistry
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string Name = name;

    public string? Registered()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) as string;
    }

    public void Register(string command)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(Name, command);
    }

    public void Unregister()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// <summary>メタ情報だけのログ(文字起こしの本文は書かない)。</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    public static string? Path { get; set; }

    public static void Write(string line)
    {
        if (Path is null) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }

    public static string Describe(UtteranceReport r) =>
        $"utterance seq={r.Seq} outcome={r.Outcome} reason={r.Reason} chars={r.Text?.Length ?? 0} fillers={r.FillersRemoved} micOpenMs={r.MicOpenMs} heldMs={r.HeldMs} releaseToDeliverMs={r.ReleaseToDeliverMs} transcribeMs={r.TranscribeMs}{(r.PeakRms is double pk ? " peakRms=" + pk.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : "")}{(r.Error is null ? "" : " error=" + r.Error)}";
}
