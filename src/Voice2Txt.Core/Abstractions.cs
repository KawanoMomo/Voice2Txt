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

    /// <summary>押下中に、それまでに録れた 16 kHz モノラルの音声の写しを返す(録音は続ける)。途中経過の文字起こしに使う。</summary>
    float[] Snapshot();

    /// <summary>直近(<see cref="Audio.MeterWindow"/> サンプル)に録れた音の大きさ(RMS、0〜1)。録音中の音量バーに使う。音が届く前・届き終えた後は 0。</summary>
    double InputRms { get; }
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

/// <summary>文字起こしエンジンの境界。途中経過は、押下中にそれまでの音声の写しを同じエンジンで文字起こしし直して作る(<paramref name="partial"/> は使わない)。</summary>
public interface ITranscriber
{
    /// <summary>確定版の文字起こし。</summary>
    Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct);

    /// <summary>
    /// 途中経過の文字起こし(押下中の写し)。エンジンはついでに、確定版が要りそうな処理(拾い直し)を先に済ませてよい
    /// (離してから届くまでに回さないため)。既定は確定版と同じ。
    /// </summary>
    Task<string> TranscribeInterimAsync(float[] samples16k, CancellationToken ct) => TranscribeAsync(samples16k, null, ct);
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
