using Voice2Txt.Core;

namespace Voice2Txt;

internal static class Program
{
    private const string MutexName = "Voice2Txt.SingleInstance";
    private const string ActivateEvent = "Voice2Txt.AlreadyRunning";

    /// <summary>
    /// 通常: タスクトレイに常駐する。
    /// 検証モード: Voice2Txt.exe --verify --scenario &lt;json&gt; --out &lt;dir&gt;
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--prepare-cuda"))
        {
            // インストーラの最後などで、CUDA の実行時ライブラリを先に取得しておく(NVIDIA の公式 redist から。SHA-256 を照合)。
            // runtime\download\ に取得元と同じ zip があればそれを使う(ネットに出ない)。
            var prov = new CudaRuntimeProvisioner(Arg(args, "--runtime-dir") ?? CudaRuntimeCatalog.DefaultDirectory);
            try
            {
                prov.EnsureAsync(new Progress<(string Phase, double Ratio)>(p => Console.Error.Write($"\r{p.Phase} {(int)(p.Ratio * 100)}%   ")), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.Error.WriteLine();
                Console.WriteLine($"cuda-runtime ready dir={prov.RuntimeDir}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cuda-runtime-error " + ex.Message);
                return 1;
            }
        }
        if (args.Contains("--verify"))
        {
            var scenario = Arg(args, "--scenario");
            var outDir = Arg(args, "--out");
            if (scenario is null || outDir is null)
            {
                Console.Error.WriteLine("usage: Voice2Txt.exe --verify --scenario <json> --out <dir>");
                return 2;
            }
            Application.Run(new VerifyHost(scenario, outDir));
            return 0;
        }

        // 多重起動: 既に動いている方に知らせて終了する
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEvent, out var ev)) { ev.Set(); ev.Dispose(); }
            return 0;
        }
        // 旧名の版が動いていれば、二つがトークキーを奪い合わないよう起動しない
        if (Mutex.TryOpenExisting(LegacyName.SingleInstanceMutex, out var legacyRunning))
        {
            legacyRunning.Dispose();
            MessageBox.Show($"旧名の版({LegacyName.Name})が動いています。トレイから終了してから {AppVersion.ProductName} を起動してください。",
                AppVersion.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        var migration = MigrateLegacy();
        var settingsPath = Path.Combine(AppSettings.DefaultDirectory, "settings.json");
        var settings = AppSettings.LoadOrCreate(settingsPath);
        AppLog.Path = Path.Combine(AppSettings.DefaultDirectory, "logs", "app.log");
        AppLog.Write($"start version={AppVersion.Tag(AppVersion.Current)}");
        if (migration is not null) AppLog.Write(migration);
        var app = new TrayApp(settings, settingsPath);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEvent);
        var wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => app.NotifyAlreadyRunning(), null, -1, false);
        Application.Run(app);
        wait.Unregister(null);
        AppLog.Write("exit");
        if (RestartRequested)
        {
            // 次の自分が多重起動と見なさないよう、先に排他を手放してから起動する
            mutex.ReleaseMutex();
            mutex.Dispose();
            activate.Dispose();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = false }); }
            catch (Exception ex) { AppLog.Write("restart-error " + ex.Message); }
        }
        return 0;
    }

    /// <summary>
    /// 旧名の置き場(%APPDATA% と %LOCALAPPDATA% の旧名フォルダ: 設定・モデル・ログ・CUDA の実行時ライブラリ)を新しい名前の置き場へ移し、
    /// 旧名のログオン時の自動起動を外す(設定 autoStart がオンなら TrayApp が新しい名前で登録し直す)。何かしたらログの 1 行を返す。
    /// </summary>
    private static string? MigrateLegacy()
    {
        try
        {
            var roaming = LegacyName.MigrateFolder(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName.Name), AppSettings.DefaultDirectory);
            var local = LegacyName.MigrateFolder(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName.Name), AppSettings.LocalDirectory);
            bool run = LegacyName.RemoveAutoStart(new RunKeyAutoStart(LegacyName.Name));
            if (!roaming.Any && !local.Any && !run) return null;
            return $"legacy-migrated roaming=({roaming}) local=({local}) autostart-removed={run}";
        }
        catch (Exception ex)
        {
            return "legacy-migrate-error " + ex.Message;
        }
    }

    /// <summary>設定画面で保存した後に「今すぐ再起動」を選んだ。終了後に自分を起動し直す。</summary>
    public static volatile bool RestartRequested;

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
