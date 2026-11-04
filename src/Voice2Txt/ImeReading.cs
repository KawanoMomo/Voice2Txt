using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Voice2Txt;

/// <summary>
/// 文の読み(かな)を Windows の日本語 IME(MSIME.Japan の IFELanguage.GetPhonetic)で得る。カスタム語彙の寄せ(<see cref="Voice2Txt.Core.KeywordBias"/>)が、
/// 寄せた文と寄せない文の読みが同じか(同音の書き分けか)を確かめるのに使う。IME は OS のもの(同梱しない)。文は外へ出ない。
/// IFELanguage は STA でしか使えないので、専用の STA スレッドで開いて使い続ける。開けなければ <see cref="TryOpen"/> が null(読みで確かめない)。
/// </summary>
internal sealed class ImeReading : IDisposable
{
    private const int TimeoutMs = 2000;
    private readonly BlockingCollection<(string Text, TaskCompletionSource<string?> Done)> _queue = new();
    private readonly Thread _thread;

    private ImeReading(Thread t) => _thread = t;

    /// <summary>IME を開く。日本語 IME が無い・開けないなら null。</summary>
    public static ImeReading? TryOpen()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ImeReading? self = null;
        var t = new Thread(() => Run(self!, ready)) { IsBackground = true, Name = "ime-reading" };
        self = new ImeReading(t);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return ready.Task.Wait(TimeoutMs) && ready.Task.Result ? self : null;
    }

    /// <summary>文の読み(かな)。分からなければ null。</summary>
    public string? Read(string text)
    {
        if (text.Length == 0) return "";
        if (_queue.IsAddingCompleted) return null;
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _queue.Add((text, done)); }
        catch (InvalidOperationException) { return null; }
        return done.Task.Wait(TimeoutMs) ? done.Task.Result : null;
    }

    private static void Run(ImeReading self, TaskCompletionSource<bool> ready)
    {
        IFELanguage? ime = null;
        try
        {
            var type = Type.GetTypeFromProgID("MSIME.Japan");
            ime = type is null ? null : Activator.CreateInstance(type) as IFELanguage;
            if (ime is null || ime.Open() != 0) { ready.TrySetResult(false); return; }
        }
        catch (Exception) { ready.TrySetResult(false); return; }
        ready.TrySetResult(true);
        try
        {
            foreach (var (text, done) in self._queue.GetConsumingEnumerable())
            {
                try { done.TrySetResult(ime.GetPhonetic(text, 1, -1, out var r) == 0 ? r : null); }
                catch (Exception) { done.TrySetResult(null); }
            }
        }
        finally
        {
            try { ime.Close(); } catch (Exception) { }
            Marshal.ReleaseComObject(ime);
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    // msime.h の IFELanguage(並びどおり。使うのは Open / Close / GetPhonetic)
    [ComImport, Guid("019F7152-E6DB-11d0-83C3-00C04FDDB82E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFELanguage
    {
        [PreserveSig] int Open();
        [PreserveSig] int Close();
        [PreserveSig] int GetJMorphResult(uint request, uint cmode, int cwchInput, [MarshalAs(UnmanagedType.LPWStr)] string input, IntPtr cinfo, out IntPtr result);
        [PreserveSig] int GetConversionModeCaps(out uint caps);
        [PreserveSig] int GetPhonetic([MarshalAs(UnmanagedType.BStr)] string text, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string result);
        [PreserveSig] int GetConversion([MarshalAs(UnmanagedType.BStr)] string text, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string result);
    }
}
