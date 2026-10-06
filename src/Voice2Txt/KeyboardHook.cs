using System.Diagnostics;
using Voice2Txt.Core;

namespace Voice2Txt;

/// <summary>
/// トークキーの低レベルフック。判定は <see cref="TalkKeyFilter"/>(検証モードと共有)に委ね、ここは結果を Windows に返すだけ。
/// フック内では重い処理をしない(呼び出し先はキューに積むだけ)。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly TalkKeyFilter _filter;
    private readonly Native.LowLevelKeyboardProc _proc; // GC されないよう保持
    private nint _hook;

    public event Action? TalkDown, TalkUp, OtherKeyWhileTalk;

    public KeyboardHook(ushort talkVk)
    {
        _filter = new TalkKeyFilter(talkVk);
        _proc = Callback;
        using var p = Process.GetCurrentProcess();
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(p.MainModule?.ModuleName), 0);
        if (_hook == 0) throw new InvalidOperationException("キーボードフックを設定できません");
    }

    private unsafe nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = *(Native.KBDLLHOOKSTRUCT*)lParam;
        if ((k.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        bool down = wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool up = wParam is Native.WM_KEYUP or Native.WM_SYSKEYUP;
        if (!down && !up) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var v = _filter.OnKey((int)k.vkCode, down);
        switch (v.Signal)
        {
            case TalkKeySignal.TalkDown: TalkDown?.Invoke(); break;
            case TalkKeySignal.TalkUp: TalkUp?.Invoke(); break;
            case TalkKeySignal.OtherKeyWhileTalk: OtherKeyWhileTalk?.Invoke(); break;
        }
        // 修飾キーとして扱う: トークキーの押下と今のキーを順に合成して送る
        if (v.InjectDown.Length > 0) Native.SendKeys(v.InjectDown.Select(vk => Native.Key((ushort)vk, false)).ToArray());
        return v.Swallow ? 1 : Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0) { Native.UnhookWindowsHookEx(_hook); _hook = 0; }
    }
}
