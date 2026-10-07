using System.Reflection;

namespace Voice2Txt.Core;

/// <summary>動いているアプリの版。exe の版はビルド時に VERSION から入る(Directory.Build.props)。画面に出すのはタグの形(v{major}.{minor})。</summary>
public static class AppVersion
{
    public const string ProductName = "Voice2Txt";

    /// <summary>"0.4.0" → "v0.4"、"0.4.1" → "v0.4.1"(タグは v{major}.{minor}。パッチがあるときだけ付ける)。読めなければ "v?"。</summary>
    public static string Tag(string? version)
    {
        var core = (version ?? "").Trim().Split('+', '-')[0];
        var parts = core.Split('.');
        if (parts.Length < 2 || parts.Any(p => !int.TryParse(p, out _))) return "v?";
        bool patch = parts.Length >= 3 && parts[2] != "0";
        return "v" + parts[0] + "." + parts[1] + (patch ? "." + parts[2] : "");
    }

    /// <summary>このアセンブリ(= exe と同じ VERSION で作られた)の版。</summary>
    public static string Current =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>トレイのメニューの先頭などに出す名前: "Voice2Txt v0.4"。</summary>
    public static string Label => $"{ProductName} {Tag(Current)}";

    /// <summary>トレイのツールチップ(Windows の上限 63 文字に切る): "Voice2Txt v0.4 — 待機中(モデル large-v3-turbo)"。</summary>
    public static string TrayText(string status)
    {
        var s = $"{Label} — {status}";
        return s.Length > 63 ? s[..63] : s;
    }
}
