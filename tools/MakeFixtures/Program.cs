using Voice2Txt.Tools;

// tools/MakeFixtures — 台本が使う合成音声の素材を test-results/fixtures/ に作る(足りないものだけ)。tools/Verify も実行前に同じことをする。
//   dotnet run --project tools/MakeFixtures [-- --out <dir>] [--only utt-13,utt-14] [--force]
// 合成した読みを表示するので、正解文と違う読みになっていないかを目で確かめる。

Console.OutputEncoding = System.Text.Encoding.UTF8;
string? outDir = null; bool force = false; var only = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--force": force = true; break;
        case "--only": only.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
        default: Console.Error.WriteLine("usage: MakeFixtures [--out <dir>] [--only a,b] [--force]"); return 2;
    }
}
if (outDir is null)
{
    var root = FixtureMaker.FindRepoRoot(Environment.CurrentDirectory) ?? FixtureMaker.FindRepoRoot(AppContext.BaseDirectory);
    if (root is null) { Console.Error.WriteLine("リポジトリ直下(Voice2Txt.sln)が見つからない。--out で置き場を指定する"); return 2; }
    outDir = Path.Combine(root, FixtureMaker.RelativeDir);
}
int made = FixtureMaker.Ensure(outDir, force, only, Console.WriteLine);
Console.WriteLine($"made={made} dir={Path.GetFullPath(outDir)}");
return 0;
