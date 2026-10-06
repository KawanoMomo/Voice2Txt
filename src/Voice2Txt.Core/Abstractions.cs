namespace Voice2Txt.Core;

// 差し替え可能な境界。本番は Windows 実装、検証モードと unit は偽物を渡す。

/// <summary>マイク。トークキーを押した瞬間に開き、離したら閉じる。</summary>
public interface IRecorder
{
    /// <summary>録音を始める。実際に音が取れ始めたら <paramref name="onStarted"/> を 1 回呼ぶ(準備中 → 録音中)。</summary>
    IRecording Start(Action onStarted);
}

public interface IRecording
{
    /// <summary>録音を止め、16 kHz モノラルの音声を返す。</summary>
    Task<float[]> StopAsync();

    /// <summary>録音を捨てる(取り消し)。</summary>
    void Abort();
}

/// <summary>前面ウィンドウ。判定はウィンドウハンドル単位。</summary>
public interface IForegroundWindow
{
    nint Current();
}

public interface IClipboard
{
    void SetText(string text);
}

/// <summary>Ctrl+V を送る。</summary>
public interface IPasteSender
{
    void SendPaste();
}

/// <summary>文字起こしエンジンの境界。途中経過は <paramref name="partial"/> に返せる形にしておく(基盤では使わない)。</summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct);
}

public interface IClock
{
    long NowMs { get; }
}

public sealed class SystemClock : IClock
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public long NowMs => _sw.ElapsedMilliseconds;
}
