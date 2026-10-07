namespace Voice2Txt.Core;

/// <summary>
/// 旧名からの引き継ぎ。製品名は <see cref="AppVersion.ProductName"/>。旧名の置き場(%APPDATA% / %LOCALAPPDATA% の旧名フォルダ)と
/// ログオン時の自動起動の旧名の値を、通常起動の最初に新しい名前へ移す。検証モードでは何もしない(利用者の置き場に触れない)。
/// </summary>
public static class LegacyName
{
    /// <summary>旧名(接頭辞 + 製品名)。公開物に旧名の文字列をそのまま残さないため連結で作る。</summary>
    public const string Name = "FF" + "_" + AppVersion.ProductName;

    /// <summary>旧名の版が動いているかを見る多重起動の名前。</summary>
    public const string SingleInstanceMutex = Name + ".SingleInstance";

    public sealed record Result(int Moved, int Kept, int Failed)
    {
        public static readonly Result None = new(0, 0, 0);
        public bool Any => Moved + Kept + Failed > 0;
        public override string ToString() => $"moved={Moved} kept={Kept} failed={Failed}";
    }

    /// <summary>
    /// legacyDir の中身を newDir へ移す。newDir が無ければフォルダごと移す。あれば 1 ファイルずつ、newDir に無いものだけ移す
    /// (newDir 側にあるものは新しい方を残す。同じ大きさ・更新時刻なら同じファイルとして旧い方を消す)。空になった旧フォルダは消す。
    /// 使用中などで移せないファイルは旧フォルダに残し、数えて返す(起動は止めない)。
    /// </summary>
    public static Result MigrateFolder(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir)) return Result.None;
        if (!Directory.Exists(newDir))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(newDir))!);
                int n = Directory.EnumerateFiles(legacyDir, "*", SearchOption.AllDirectories).Count();
                Directory.Move(legacyDir, newDir);
                return new Result(n, 0, 0);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        int moved = 0, kept = 0, failed = 0;
        foreach (var src in Directory.EnumerateFiles(legacyDir, "*", SearchOption.AllDirectories).ToList())
        {
            var dst = Path.Combine(newDir, Path.GetRelativePath(legacyDir, src));
            try
            {
                if (File.Exists(dst))
                {
                    if (SameFile(src, dst)) { File.Delete(src); moved++; }
                    else kept++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst);
                moved++;
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        RemoveEmptyDirs(legacyDir);
        return new Result(moved, kept, failed);
    }

    /// <summary>旧名で登録されたログオン時の自動起動を外す(登録し直すのは設定 autoStart に従う <see cref="AutoStart.Sync"/>)。外したら true。</summary>
    public static bool RemoveAutoStart(IAutoStartRegistry legacy)
    {
        if (legacy.Registered() is null) return false;
        legacy.Unregister();
        return true;
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }

    private static void RemoveEmptyDirs(string dir)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir)) RemoveEmptyDirs(d);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
