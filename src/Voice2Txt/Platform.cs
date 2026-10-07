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
    }
}

internal sealed class Win32Foreground : IForegroundWindow
{
    public nint Current() => Native.GetForegroundWindow();
}

/// <summary>
/// クリップボードは STA の UI スレッドで触る。元の内容には戻さない(Win+V の履歴で取り出す運用)。
/// 1 回の呼び出しでは短く試すだけにし(UI を止めない)、入らなければ投げる。粘るのは PushToTalkController。
/// </summary>
internal sealed class Win32Clipboard(Control ui) : IClipboard
{
    public void SetText(string text) => ui.Invoke(() =>
        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, retryTimes: 3, retryDelay: 20));
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
internal sealed class RunKeyAutoStart : IAutoStartRegistry
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Voice2Txt";

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
        $"utterance seq={r.Seq} outcome={r.Outcome} reason={r.Reason} chars={r.Text?.Length ?? 0} micOpenMs={r.MicOpenMs} heldMs={r.HeldMs} releaseToDeliverMs={r.ReleaseToDeliverMs} transcribeMs={r.TranscribeMs}{(r.Error is null ? "" : " error=" + r.Error)}";
}
