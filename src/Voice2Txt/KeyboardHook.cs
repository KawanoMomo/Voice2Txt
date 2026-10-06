using System.Diagnostics;

namespace Voice2Txt;

/// <summary>
/// トークキーの低レベルフック。トークキーは握りつぶし、操作中のアプリへ渡さない。
/// 押下中に別のキーが押されたら、発話を取り消し、トークキーを修飾キーとして押されたことにして(合成した押下に続けて同じキーを送り)ショートカットを効かせる。
/// フック内では重い処理をしない(呼び出し先はキューに積むだけ)。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly ushort _talkVk;
    private readonly Native.LowLevelKeyboardProc _proc; // GC されないよう保持
    private nint _hook;
    private bool _talkDown, _passThrough;

    public event Action? TalkDown, TalkUp, OtherKeyWhileTalk;

    public KeyboardHook(ushort talkVk)
    {
        _talkVk = talkVk;
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

        if (k.vkCode == _talkVk)
        {
            if (down)
            {
                if (_passThrough) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                if (!_talkDown) { _talkDown = true; TalkDown?.Invoke(); }
                return 1; // 握りつぶす(リピートも)
            }
            if (up)
            {
                _talkDown = false;
                if (_passThrough) { _passThrough = false; return Native.CallNextHookEx(_hook, nCode, wParam, lParam); }
                TalkUp?.Invoke();
                return 1;
            }
        }
        else if (down && _talkDown && !_passThrough)
        {
            // 修飾キーとして扱う: 発話を取り消し、トークキーの押下と今のキーを順に合成して送る
            _passThrough = true;
            OtherKeyWhileTalk?.Invoke();
            Native.SendKeys(Native.Key(_talkVk, false), Native.Key((ushort)k.vkCode, false));
            return 1;
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0) { Native.UnhookWindowsHookEx(_hook); _hook = 0; }
    }
}
