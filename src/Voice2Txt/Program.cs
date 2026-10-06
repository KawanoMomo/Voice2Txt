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
        var settingsPath = Path.Combine(AppSettings.DefaultDirectory, "settings.json");
        var settings = AppSettings.LoadOrCreate(settingsPath);
        AppLog.Path = Path.Combine(AppSettings.DefaultDirectory, "logs", "app.log");
        AppLog.Write("start");
        var app = new TrayApp(settings, settingsPath);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEvent);
        var wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => app.NotifyAlreadyRunning(), null, -1, false);
        Application.Run(app);
        wait.Unregister(null);
        AppLog.Write("exit");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
