namespace Voice2Txt.Core;

/// <summary>ログオン時の自動起動の登録先(本番は HKCU の Run)。</summary>
public interface IAutoStartRegistry
{
    /// <summary>登録されているコマンド(無ければ null)。</summary>
    string? Registered();
    void Register(string command);
    void Unregister();
}

/// <summary>設定 autoStart と登録を一致させる。</summary>
public static class AutoStart
{
    public static string CommandFor(string exePath) => $"\"{exePath}\"";

    /// <summary>設定どおりに登録・解除する。exe の場所が変わっていれば登録し直す。何かを変えたら true。</summary>
    public static bool Sync(bool enabled, string exePath, IAutoStartRegistry reg)
    {
        var want = CommandFor(exePath);
        var have = reg.Registered();
        if (enabled && !string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) { reg.Register(want); return true; }
        if (!enabled && have is not null) { reg.Unregister(); return true; }
        return false;
    }

    /// <summary>トレイのメニューから切り替える: 設定を反転して保存し、登録を合わせる。</summary>
    public static bool Toggle(AppSettings s, string settingsPath, string exePath, IAutoStartRegistry reg)
    {
        s.AutoStart = !s.AutoStart;
        s.Save(settingsPath);
        Sync(s.AutoStart, exePath, reg);
        return s.AutoStart;
    }
}
