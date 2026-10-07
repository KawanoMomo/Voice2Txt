using System.Diagnostics;
using System.Text.Json;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

// tools/Verify — 検証モードの台本(tests/scenarios/*.json)を順に実行し、期待と比べて passed / failed を出す。
//   dotnet run --project tools/Verify -- --exe <Voice2Txt.exe> --scenarios <file|dir>... --out <dir>
// 終了コード: 全部 passed なら 0、1 件でも failed なら 1、使い方の誤りは 2。

Console.OutputEncoding = System.Text.Encoding.UTF8;
string? exe = null, outDir = null;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--exe": exe = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--scenarios":
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) inputs.Add(args[++i]);
            break;
    }
}
if (exe is null || outDir is null || inputs.Count == 0)
{
    Console.Error.WriteLine("usage: Verify --exe <Voice2Txt.exe> --scenarios <file|dir>... --out <dir>");
    return 2;
}

var files = inputs.SelectMany(p => Directory.Exists(p)
        ? Directory.GetFiles(p, "*.json").OrderBy(f => f, StringComparer.Ordinal)
        : (IEnumerable<string>)[p])
    .Select(Path.GetFullPath).ToList();
outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);

var summary = new List<object>();
int passed = 0, failed = 0;
foreach (var file in files)
{
    var name = Path.GetFileNameWithoutExtension(file);
    var dir = Path.Combine(outDir, name);
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var sw = Stopwatch.StartNew();
    List<string> fails;
    try
    {
        var sc = Scenario.Load(file);
        if (sc.Expect.VersionFile is { } vf) sc.Expect.Version ??= AppVersion.Tag(File.ReadAllText(Scenario.ResolvePath(file, vf)));
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--verify", "--scenario", file, "--out", dir },
            UseShellExecute = false,
        })!;
        if (!p.WaitForExit(sc.TimeoutMs + 60_000))
        {
            p.Kill(entireProcessTree: true);
            fails = [$"{sc.TimeoutMs + 60_000} ms で終わらないので止めた"];
        }
        else
        {
            var resultPath = Path.Combine(dir, "result.json");
            fails = File.Exists(resultPath)
                ? Evaluator.Check(sc.Expect, VerifyResult.Load(resultPath), s => File.Exists(Path.Combine(dir, s)))
                : [$"result.json が無い(終了コード {p.ExitCode})"];
        }
    }
    catch (Exception ex) { fails = [$"実行できない: {ex.Message}"]; }

    if (fails.Count == 0) { passed++; Console.WriteLine($"passed: {name} ({sw.Elapsed.TotalSeconds:0.0}s)"); }
    else
    {
        failed++;
        Console.WriteLine($"failed: {name} ({sw.Elapsed.TotalSeconds:0.0}s)");
        foreach (var f in fails) Console.WriteLine($"  - {f}");
    }
    summary.Add(new { name, passed = fails.Count == 0, failures = fails, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
}
Console.WriteLine($"passed={passed} failed={failed}");
File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(new { passed, failed, scenarios = summary }, AppSettings.Json));
return failed == 0 ? 0 : 1;
