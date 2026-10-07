using System.Threading.Channels;

namespace Voice2Txt.Core;

/// <summary>Failed = 確定版はできたが届けられなかった(クリップボードに入れられない)。退避とは違い、Ctrl+V では貼れない。</summary>
public enum Outcome { Pasted, Evacuated, Cancelled, Failed }

/// <summary>取り消しの理由(ログ・結果 JSON に書く。本文は書かない)。</summary>
public enum CancelReason { None, TooShort, OtherKey, Silence, NoText, ModelNotReady, Error, ClipboardBusy }

/// <summary>1 つの発話の結末。時間はすべて ms。FillersRemoved は確定版から取り除いた言い淀みの数。
/// PeakRms は録音の最も大きい 30 ms 区間の RMS(録音が届いた発話だけ。無音の取り消しが声を落としていないかをログで確かめる)。</summary>
public sealed record UtteranceReport(
    int Seq, Outcome Outcome, CancelReason Reason, string? Text,
    long PressAtMs, long? MicOpenMs, long HeldMs, long? ReleaseToDeliverMs, long? TranscribeMs, string? Error = null, int FillersRemoved = 0,
    double? PeakRms = null);

/// <param name="ClipboardRetryMs">他アプリがクリップボードを開いたままのとき、確定版を入れるまで粘る時間。その間、後の発話は順番を待つ。</param>
/// <param name="ClipboardRetryIntervalMs">粘る間の試行の間隔。</param>
/// <param name="PasteSettleMs">Ctrl+V を送ってから次の確定版でクリップボードを上書きするまで空ける最短の時間(貼り付け先がクリップボードを読むのは
/// 自分の入力を処理した時で、Ctrl+V を送った時ではない)。前の Ctrl+V の後に押した発話は押下だけでこれ以上経つのがふつうで、待つのは処理待ちの発話。</param>
/// <param name="Fillers">確定版から取り除く言い淀みの語(<see cref="Core.Fillers"/>)。null か空なら取り除かない。</param>
/// <param name="InterimIntervalMs">押下中に途中経過を作り直す間隔(前の作り直しが終わってから次までの ms)。0 なら途中経過を出さない。</param>
public sealed record PttOptions(double MinPressSeconds = 0.3, double SilenceThreshold = 0.01,
    int ClipboardRetryMs = 3000, int ClipboardRetryIntervalMs = 50, int PasteSettleMs = 500, IReadOnlyList<string>? Fillers = null,
    int InterimIntervalMs = 0)
{
    /// <summary>途中経過を作り直す間隔の既定(MOC の「約 0.7 秒ごと」)。</summary>
    public const int DefaultInterimIntervalMs = 700;

    public static PttOptions From(AppSettings s) => new(s.MinPressSeconds, s.SilenceThreshold,
        Fillers: s.RemoveFillers ? (s.Fillers ?? []) : null,
        InterimIntervalMs: s.ShowInterim ? DefaultInterimIntervalMs : 0);
}

public sealed record PttDependencies(
    IRecorder Recorder, IForegroundWindow Foreground, IClipboard Clipboard, IPasteSender Paster,
    ITranscriber Transcriber, IClock Clock);

/// <summary>
/// プッシュトゥトーク音声入力の中核。トークキーの押下・離し・他キーを受け、発話を録音した順に処理して届ける。
/// スレッドセーフ。イベントはロック内で上げるので、受け手は処理を UI スレッドへ投げるだけにする(ブロックしない)。
/// </summary>
public sealed class PushToTalkController : IAsyncDisposable
{
    private sealed record Ctx(int Seq, nint Target, long PressAt, long? StartedAt, long ReleasedAt);

    private readonly PttDependencies _d;
    private readonly PttOptions _o;
    private readonly object _gate = new();
    private readonly Channel<(Ctx Ctx, Task<float[]> Audio)> _queue = Channel.CreateUnbounded<(Ctx, Task<float[]>)>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private System.Diagnostics.Stopwatch? _sincePaste;   // 最後に Ctrl+V を送ってから(worker だけが触る)

    private bool _held, _started, _modelHeld, _modelReady;
    private string? _modelDetail;
    private int _seq, _pending;
    private long _pressAt, _startedAt;
    private nint _target;
    private IRecording? _rec;
    private CancellationTokenSource? _interimCts;   // 押下中の途中経過の作り直し(離す・取り消しで止める)
    private string? _interim;                       // 最後に押した発話の途中経過(その確定版の結末が出たら消す)
    private int _interimSeq;
    private OverlayView? _transient;
    private OverlayView _last = OverlayView.Hidden;

    public event Action<OverlayView>? OverlayChanged;
    public event Action<UtteranceReport>? Finished;

    public PushToTalkController(PttDependencies deps, PttOptions options)
    {
        _d = deps; _o = options;
        _worker = Task.Run(WorkerAsync);
    }

    public OverlayView CurrentView { get { lock (_gate) return _last; } }

    /// <summary>録音中の音量バーの値(0〜1、<see cref="InputMeter.Level"/>)。押していない・まだ音が届いていないときは 0。</summary>
    public double InputLevel
    {
        get
        {
            IRecording? rec;
            lock (_gate) rec = _held && _started ? _rec : null;
            return rec is null ? 0 : InputMeter.Level(rec.InputRms);
        }
    }

    /// <summary>モデルの準備状況。準備中にトークキーが押されたら「モデル準備中」を出し、録音しない。</summary>
    public void SetModelStatus(bool ready, string? detail = null)
    {
        lock (_gate) { _modelReady = ready; _modelDetail = detail; Emit(); }
    }

    public void OnTalkKeyDown()
    {
        lock (_gate)
        {
            if (_held || _modelHeld) return; // キーリピート
            _transient = null;
            if (!_modelReady) { _modelHeld = true; Emit(); return; }
            _held = true; _started = false;
            int seq = ++_seq;
            _interim = null; _interimSeq = seq;
            _pressAt = _d.Clock.NowMs;
            _target = _d.Foreground.Current();
            Emit();
            _rec = _d.Recorder.Start(() =>
            {
                lock (_gate)
                {
                    if (!_held || _seq != seq || _started) return;
                    _started = true; _startedAt = _d.Clock.NowMs; Emit();
                    StartInterim(seq);
                }
            });
        }
    }

    /// <summary>トークキーを押している間に別のキーが押された。発話は取り消し(キーは修飾キーとして通すのはホストの役目)。</summary>
    public void OnOtherKeyDown()
    {
        lock (_gate)
        {
            if (!_held) return;
            _held = false;
            StopInterim(); _interim = null;
            _rec?.Abort(); _rec = null;
            long now = _d.Clock.NowMs;
            Report(new(_seq, Outcome.Cancelled, CancelReason.OtherKey, null, _pressAt, MicOpen(), now - _pressAt, null, null));
            Emit();
        }
    }

    public void OnTalkKeyUp()
    {
        lock (_gate)
        {
            if (_modelHeld)
            {
                _modelHeld = false;
                Report(new(0, Outcome.Cancelled, CancelReason.ModelNotReady, null, _d.Clock.NowMs, null, 0, null, null));
                Emit();
                return;
            }
            if (!_held) return;
            _held = false;
            // 途中経過の作り直しを止める(確定版の文字起こしを待たせない)。最後の途中経過は処理中の間も出しておく
            StopInterim();
            var rec = _rec; _rec = null;
            long now = _d.Clock.NowMs, held = now - _pressAt;
            var ctx = new Ctx(_seq, _target, _pressAt, _started ? _startedAt : null, now);
            if (held < _o.MinPressSeconds * 1000 || rec is null)
            {
                rec?.Abort();
                _interim = null;
                _transient = new OverlayView(OverlayState.Cancelled);
                Report(new(ctx.Seq, Outcome.Cancelled, CancelReason.TooShort, null, ctx.PressAt, MicOpen(), held, null, null));
                Emit();
                return;
            }
            _pending++;
            _queue.Writer.TryWrite((ctx, rec.StopAsync()));
            Emit();
        }
    }

    /// <summary>押下中でなく、処理待ちが 0 になるまで待つ。</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_gate) if (!_held && !_modelHeld && _pending == 0) return true;
            await Task.Delay(30);
        }
        return false;
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var (ctx, audio) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                float[] samples;
                try { samples = await audio; }
                catch (Exception ex) { Finish(ctx, Outcome.Cancelled, CancelReason.Error, null, null, new(OverlayState.Cancelled, 0, "録音に失敗しました"), ex.Message); continue; }

                // 声の有無は録音の全体で決める(途中経過が出たかどうかには依らない)。小さい声も無音として落とさない(Audio.HasVoice)
                double peak = Audio.PeakFrameRms(samples);
                if (!Audio.HasVoice(samples, _o.SilenceThreshold))
                {
                    Finish(ctx, Outcome.Cancelled, CancelReason.Silence, null, null, new(OverlayState.CancelledSilence), peak: peak);
                    continue;
                }
                // 前後と発話の間の長い無音を詰めてから復号に渡す(無音が長いと音声に無い文が足される)
                samples = Audio.TrimSilence(samples, _o.SilenceThreshold);
                long t0 = _d.Clock.NowMs;
                string text;
                try { text = (await _d.Transcriber.TranscribeAsync(samples, null, _cts.Token)).Trim(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Finish(ctx, Outcome.Cancelled, CancelReason.Error, null, _d.Clock.NowMs - t0, new(OverlayState.Cancelled, 0, "文字起こしに失敗しました"), ex.Message, peak: peak); continue; }
                long tMs = _d.Clock.NowMs - t0;
                // 言い淀みを取り除いてから届ける(設定で止められる)。言い淀みだけの発話は空になり、取り消し(無音)になる。
                int fillers = 0;
                if (_o.Fillers is { Count: > 0 } words) (text, fillers) = Core.Fillers.Remove(text, words);
                if (text.Length == 0)
                {
                    Finish(ctx, Outcome.Cancelled, CancelReason.NoText, null, tMs, new(OverlayState.CancelledSilence), fillers: fillers, peak: peak);
                    continue;
                }
                // 前の発話の Ctrl+V を貼り付け先が読み終える前に上書きしない(処理待ちの発話だけがここで待つ)。
                if (!await SettleAfterPasteAsync()) return;
                // 確定版をクリップボードへ(入るまで粘る)。入らなければ「Ctrl+V で貼れます」とは言わず、入力失敗を知らせる。
                var clipError = await PutOnClipboardAsync(text);
                if (clipError is not null)
                {
                    Finish(ctx, Outcome.Failed, CancelReason.ClipboardBusy, null, tMs, new(OverlayState.DeliveryFailed), clipError, fillers, peak);
                    continue;
                }
                // 入った後は、貼り付け先ウィンドウが前面のときだけ Ctrl+V、違えば退避(確定版はクリップボードにある)。
                Outcome outcome;
                try
                {
                    if (_d.Foreground.Current() == ctx.Target)
                    {
                        _sincePaste = System.Diagnostics.Stopwatch.StartNew();
                        _d.Paster.SendPaste(); outcome = Outcome.Pasted;
                    }
                    else outcome = Outcome.Evacuated;
                }
                catch (Exception ex) { Finish(ctx, Outcome.Evacuated, CancelReason.Error, text, tMs, new(OverlayState.Evacuated), ex.Message, fillers, peak); continue; }
                Finish(ctx, outcome, CancelReason.None, text, tMs,
                    new(outcome == Outcome.Pasted ? OverlayState.Pasted : OverlayState.Evacuated), fillers: fillers, peak: peak);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>直前の Ctrl+V から <see cref="PttOptions.PasteSettleMs"/> 経つまで待つ。経っていれば待たない。止められたら false。</summary>
    private async Task<bool> SettleAfterPasteAsync()
    {
        if (_sincePaste is null) return true;
        long rest = _o.PasteSettleMs - _sincePaste.ElapsedMilliseconds;
        if (rest <= 0) return true;
        try { await Task.Delay((int)rest, _cts.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>確定版をクリップボードへ入れる。入れば null、粘っても入らなければ最後の失敗の理由。</summary>
    private async Task<string?> PutOnClipboardAsync(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            try { _d.Clipboard.SetText(text); return null; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (sw.ElapsedMilliseconds >= _o.ClipboardRetryMs || _cts.IsCancellationRequested)
                    return $"clipboard attempts={attempt} ms={sw.ElapsedMilliseconds} {ex.GetType().Name}: {ex.Message}";
            }
            try { await Task.Delay(_o.ClipboardRetryIntervalMs, _cts.Token); }
            catch (OperationCanceledException) { return $"clipboard attempts={attempt} cancelled"; }
        }
    }

    private void Finish(Ctx ctx, Outcome o, CancelReason r, string? text, long? transcribeMs, OverlayView transient, string? error = null, int fillers = 0, double? peak = null)
    {
        lock (_gate)
        {
            _pending--;
            if (ctx.Seq == _interimSeq) _interim = null;
            _transient = transient;
            long now = _d.Clock.NowMs;
            Report(new(ctx.Seq, o, r, text, ctx.PressAt, ctx.StartedAt - ctx.PressAt, ctx.ReleasedAt - ctx.PressAt,
                o is Outcome.Cancelled or Outcome.Failed ? null : now - ctx.ReleasedAt, transcribeMs, error, fillers, peak));
            Emit();
        }
    }

    private long? MicOpen() => _started ? _startedAt - _pressAt : null;

    private void Report(UtteranceReport r) => Finished?.Invoke(r);

    private OverlayView Compute()
    {
        if (_modelHeld) return new(OverlayState.ModelPreparing, 0, _modelDetail);
        if (_held)
            return !_started ? new(OverlayState.Preparing, _pending)
                : _pending > 0 ? new(OverlayState.ProcessingAndRecording, _pending, null, _interim)
                : new(OverlayState.Recording, 0, null, _interim);
        if (_pending > 0) return new(OverlayState.Processing, _pending, null, _interim);
        return _transient ?? OverlayView.Hidden;
    }

    /// <summary>録音中になった発話の途中経過の作り直しを始める(ロック内で呼ぶ)。</summary>
    private void StartInterim(int seq)
    {
        if (_o.InterimIntervalMs <= 0) return;
        StopInterim();
        var cts = _interimCts = new CancellationTokenSource();
        _ = Task.Run(() => InterimLoopAsync(seq, cts.Token));
    }

    private void StopInterim()
    {
        _interimCts?.Cancel();
        _interimCts = null;
    }

    /// <summary>
    /// 押下中、一定間隔でそれまでの音声の写しを頭から文字起こしし直し、途中経過としてオーバーレイにだけ出す(届けない)。
    /// 前の発話の確定版を処理している間(処理待ちがある間)は作らない: エンジンは 1 つで、確定版を待たせないため。
    /// </summary>
    private async Task InterimLoopAsync(int seq, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_o.InterimIntervalMs, ct);
                IRecording? rec;
                lock (_gate)
                {
                    if (!_held || _seq != seq) return;
                    rec = _rec;
                }
                if (rec is null || Volatile.Read(ref _pending) > 0) continue;
                var samples = rec.Snapshot();
                if (samples.Length < Audio.SampleRate / 2 || !Audio.HasVoice(samples, _o.SilenceThreshold)) continue;
                samples = Audio.TrimSilence(samples, _o.SilenceThreshold);
                string text;
                try { text = (await _d.Transcriber.TranscribeInterimAsync(samples, ct)).Trim(); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { continue; } // 途中経過は出せなくても録音と確定版には響かせない
                // 確定版と同じく言い淀みを除いて見せる(押下中に「ええ」が見えて、届いた確定版から消えている、という食い違いを出さない)
                if (_o.Fillers is { Count: > 0 } words) text = Core.Fillers.Remove(text, words).Text;
                lock (_gate)
                {
                    if (ct.IsCancellationRequested || !_held || _seq != seq) return;
                    if (text.Length == 0 || text == _interim) continue;
                    _interim = text;
                    Emit();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Emit()
    {
        var v = Compute();
        if (v == _last) return;
        _last = v;
        OverlayChanged?.Invoke(v);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        lock (_gate) StopInterim();
        _cts.Cancel();
        try { await _worker; } catch { }
        _cts.Dispose();
    }
}
