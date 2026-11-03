namespace Voice2Txt.Core;

/// <summary>
/// 録音のしかた(設定 <see cref="AppSettings.RecordMode"/>): hold(初期値。トークキーを押している間だけ録音)/
/// toggle(押さない録音。開始キーで録音を始め、終了キーで止めて文字起こしする。録音中に他のキー・マウスを使っても録音は途切れない)。
/// </summary>
public static class RecordModes
{
    public const string Hold = "hold";
    public const string Toggle = "toggle";

    public static readonly IReadOnlyList<SettingChoice> Choices =
    [
        new(Hold, "押している間だけ録音(既定)"),
        new(Toggle, "押さない録音"),
    ];

    /// <summary>設定の値を読む(大文字小文字・前後の空白・画面の名前も可。空・無しは hold)。読めなければ null。</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Hold;
        return Choices.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Label, v, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>押さない録音か(読めない値は hold。呼び出し側は <see cref="InvalidWarning"/> を知らせる)。</summary>
    public static bool IsToggle(string? setting) => Normalize(setting) == Toggle;

    /// <summary>recordMode を読めないときの知らせ(読めれば null)。</summary>
    public static string? InvalidWarning(string? setting) => Normalize(setting) is null
        ? $"設定の recordMode「{setting}」を読めないため、押している間だけ録音します(選べる値: {string.Join(", ", Choices.Select(c => c.Value))})"
        : null;
}

/// <summary>修飾キー(どちらの左右でも同じ)。</summary>
[Flags]
public enum ChordModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>
/// 押さない録音の開始キー・終了キー: 修飾キー(Ctrl / Alt / Shift / Win)と 1 つのキーの組み合わせ(例 Ctrl+Alt+Space)。仮想キーコードで持つ。
/// 文字・数字・Space などの入力に使うキーは、Ctrl・Alt・Win のどれかと組み合わせなければならない(ふつうの入力とぶつからない)。F1〜F24・Pause・ScrollLock は単独でもよい。
/// </summary>
public sealed record KeyChord(ChordModifiers Modifiers, int Vk)
{
    public const string DefaultStart = "Ctrl+Alt+Space";
    public const string DefaultStop = "Ctrl+Alt+Space";

    /// <summary>設定画面の選択肢(他の操作とぶつかりにくい組み合わせ)。設定ファイルには読める組み合わせなら何でも書ける。</summary>
    public static readonly IReadOnlyList<SettingChoice> Choices =
    [
        new("Ctrl+Alt+Space", "Ctrl+Alt+Space(既定)"),
        new("Ctrl+Alt+R", "Ctrl+Alt+R"),
        new("Ctrl+Alt+S", "Ctrl+Alt+S"),
        new("Ctrl+Alt+E", "Ctrl+Alt+E"),
        new("Ctrl+Shift+Space", "Ctrl+Shift+Space"),
        new("Ctrl+Alt+F9", "Ctrl+Alt+F9"),
        new("Ctrl+Alt+F10", "Ctrl+Alt+F10"),
        new("Pause", "Pause"),
    ];

    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["Prior"] = 0x21,
        ["PageDown"] = 0x22, ["Next"] = 0x22, ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["Scroll"] = 0x91,
    };

    /// <summary>単独でもよいキー(F1〜F24・Pause・ScrollLock)。</summary>
    private static bool StandsAlone(int vk) => vk is >= 0x70 and <= 0x87 or 0x13 or 0x91;

    /// <summary>修飾キーの仮想キーコード(左右・区別なしの両方)なら、その修飾。違えば None。</summary>
    public static ChordModifiers ModifierOf(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => ChordModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => ChordModifiers.Alt,
        0x10 or 0xA0 or 0xA1 => ChordModifiers.Shift,
        0x5B or 0x5C => ChordModifiers.Win,
        _ => ChordModifiers.None,
    };

    /// <summary>「Ctrl+Alt+Space」の形を読む(大文字小文字・空白は問わない。修飾は Ctrl / Control / Alt / Shift / Win)。読めなければ false。</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = new(ChordModifiers.None, 0);
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) return false;
        var mods = ChordModifiers.None;
        foreach (var p in parts[..^1])
        {
            var m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ChordModifiers.Ctrl,
                "alt" => ChordModifiers.Alt,
                "shift" => ChordModifiers.Shift,
                "win" or "windows" => ChordModifiers.Win,
                _ => ChordModifiers.None,
            };
            if (m == ChordModifiers.None || (mods & m) != 0) return false;
            mods |= m;
        }
        var key = parts[^1];
        int vk;
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) vk = char.ToUpperInvariant(key[0]);
        else if (key.Length == 1 && char.IsAsciiDigit(key[0])) vk = key[0];
        else if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = 0x70 + f - 1;
        else if (!Named.TryGetValue(key, out vk)) return false;
        // 入力に使うキーを Shift だけ・単独で使うと、ふつうの入力とぶつかる
        if (!StandsAlone(vk) && (mods & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) == 0) return false;
        chord = new(mods, vk);
        return true;
    }

    /// <summary>設定ファイル・画面に出す形(修飾は Ctrl, Alt, Shift, Win の順)。</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ChordModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(Vk));
        return string.Join("+", parts);
    }

    private static string KeyName(int vk) => vk switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
        _ => Named.Where(kv => kv.Value == vk).Select(kv => kv.Key).FirstOrDefault() ?? $"0x{vk:X2}",
    };

    /// <summary>設定の値を読む。読めなければ <paramref name="fallback"/> を使い、知らせる文を <paramref name="warning"/> に返す。</summary>
    public static KeyChord ParseOr(string? text, string fallback, string settingKey, out string? warning)
    {
        warning = null;
        if (TryParse(text, out var c)) return c;
        TryParse(fallback, out c);
        warning = $"設定の {settingKey}「{text}」を読めないため {fallback} を使います(例 Ctrl+Alt+Space。文字・数字・Space は Ctrl か Alt と組み合わせます)";
        return c;
    }
}

/// <summary>キーボードフックと検証モードが共有する、キー 1 つの判定。</summary>
public interface ITalkKeyFilter
{
    KeyVerdict OnKey(int vk, bool down);
}

/// <summary>開始キー・終了キーで録音する判定(押さない録音の <see cref="ToggleKeyFilter"/> と、押している間だけ録音の <see cref="HoldKeyFilter"/>)。</summary>
public interface IChordRecording
{
    /// <summary>開始キーで始めた録音の最中か(直前の <see cref="TalkKeySignal.TalkDown"/> が開始キーによるものか)。</summary>
    bool ChordRecording { get; }

    /// <summary>終了キー(録音中の案内に出す)。</summary>
    KeyChord Stop { get; }

    /// <summary>録音が始まらなかった(モデル準備中など)ので、次の開始キーをもう一度開始として受ける。</summary>
    void Reset();
}

/// <summary>
/// 押している間だけ録音(hold)のキーの判定: トークキーは <see cref="TalkKeyFilter"/> のとおり。加えて、トークキーを押していないときに開始キーの組み合わせを押すと、
/// その発話だけ押さない録音になる(その場の押さない録音。終了キーで止めて文字起こし。録音中は他のキー・トークキーを素通しして取り消さない)。
/// 止めた後はトークキーの押している間だけ録音に戻る。設定の切り替えも再起動も要らない。
/// トークキーを押している間の開始キーは、他のキーと同じく発話の取り消し(トークキーの判定を優先する)。
/// </summary>
public sealed class HoldKeyFilter(int talkVk, KeyChord start, KeyChord stop) : ITalkKeyFilter, IChordRecording
{
    private readonly TalkKeyFilter _talk = new(talkVk);
    private readonly ToggleKeyFilter _chord = new(start, stop);

    public int TalkVk => talkVk;
    public KeyChord Start => start;
    public KeyChord Stop => stop;
    public bool ChordRecording => _chord.Recording;
    public void Reset() => _chord.Reset();

    public KeyVerdict OnKey(int vk, bool down)
    {
        bool modifier = KeyChord.ModifierOf(vk) != ChordModifiers.None;
        if (modifier) _chord.OnKey(vk, down); // 修飾キーの押下の状態だけ追う(判定は素通し)
        if (_chord.Recording)
        {
            // トークキーを修飾キーとして開始キーを押した(押下中に他のキーで取り消し済み)ときの離しは、トークキーの判定の後始末だけ(素通し)
            if (vk == talkVk && !down && _talk.Down) return _talk.OnKey(vk, down) with { Signal = TalkKeySignal.None };
            return modifier ? KeyVerdict.Pass : _chord.OnKey(vk, down);
        }
        if (vk == talkVk || _talk.Talking) return _talk.OnKey(vk, down);
        if (modifier) return _talk.OnKey(vk, down);
        var c = _chord.OnKey(vk, down);
        return c.Swallow || c.Signal != TalkKeySignal.None ? c : _talk.OnKey(vk, down);
    }
}

/// <summary>
/// 押さない録音のキーの判定: 開始キーの組み合わせで <see cref="TalkKeySignal.TalkDown"/>(録音を始める)、録音中に終了キーの組み合わせで
/// <see cref="TalkKeySignal.TalkUp"/>(止めて文字起こし)。開始キーと終了キーが同じなら押すたびに切り替わる。
/// 組み合わせの最後のキー(押下・リピート・離し)は操作中のアプリへ渡さない。修飾キーと他のキーはすべて素通しし、録音中に押しても録音は取り消さない
/// (押している間だけ録音するトークキーとの違い)。修飾は過不足なく一致したときだけ(Ctrl+Alt+Shift+Space は Ctrl+Alt+Space ではない)。
/// </summary>
public sealed class ToggleKeyFilter(KeyChord start, KeyChord stop) : ITalkKeyFilter, IChordRecording
{
    bool IChordRecording.ChordRecording => _recording;

    private readonly HashSet<int> _mods = [];
    private int _swallowing;           // 握りつぶし中の組み合わせの最後のキー(離すまで。0 なら無し)
    private volatile bool _recording;

    public KeyChord Start => start;
    public KeyChord Stop => stop;

    /// <summary>録音中と見ているか(開始キーで true、終了キーで false)。</summary>
    public bool Recording => _recording;

    /// <summary>録音が始まらなかった(モデル準備中など)ので、次の開始キーをもう一度開始として受ける。</summary>
    public void Reset() => _recording = false;

    public KeyVerdict OnKey(int vk, bool down)
    {
        var m = KeyChord.ModifierOf(vk);
        if (m != ChordModifiers.None)
        {
            if (down) _mods.Add(vk); else _mods.Remove(vk);
            return KeyVerdict.Pass;
        }
        if (!down)
        {
            if (vk != _swallowing) return KeyVerdict.Pass;
            _swallowing = 0;
            return new(true, TalkKeySignal.None, []);
        }
        if (vk == _swallowing) return new(true, TalkKeySignal.None, []); // リピート
        var held = _mods.Aggregate(ChordModifiers.None, (a, k) => a | KeyChord.ModifierOf(k));
        if (!_recording && Matches(start, vk, held))
        {
            _recording = true; _swallowing = vk;
            return new(true, TalkKeySignal.TalkDown, []);
        }
        if (_recording && Matches(stop, vk, held))
        {
            _recording = false; _swallowing = vk;
            return new(true, TalkKeySignal.TalkUp, []);
        }
        // 録音中の開始キー(終了キーと違うとき)は、もう始まっているので何もしないが、アプリにも渡さない
        if (_recording && Matches(start, vk, held)) { _swallowing = vk; return new(true, TalkKeySignal.None, []); }
        return KeyVerdict.Pass;
    }

    private static bool Matches(KeyChord c, int vk, ChordModifiers held) => c.Vk == vk && c.Modifiers == held;
}
