using System.IO.Compression;
using System.Security.Cryptography;

namespace Voice2Txt.Core;

/// <summary>NVIDIA の CUDA 実行時ライブラリの取得単位(redist の zip 1 つ)。Dlls は zip の中から取り出すファイル名。</summary>
public sealed record CudaArchive(string Name, string Url, string Sha256, long Size, IReadOnlyList<string> Dlls);

/// <summary>
/// CUDA の実行時ライブラリ(cudart / cuBLAS)は配布物に入れず、利用者の PC が初回に NVIDIA の公式 redist から取得する(後入れ)。
/// 取得元・版・ハッシュはここに固定する(THIRD_PARTY_NOTICES.md と同じ)。置き場は %LOCALAPPDATA%\Voice2Txt\runtime。
/// </summary>
public static class CudaRuntimeCatalog
{
    private const string Source = "https://developer.download.nvidia.com/compute/cuda/redist/";

    public static readonly IReadOnlyList<CudaArchive> Archives =
    [
        new("cuda_cudart-windows-x86_64-13.0.96-archive",
            Source + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip",
            "a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4", 2873447, ["cudart64_13.dll"]),
        new("libcublas-windows-x86_64-13.1.0.3-archive",
            Source + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.0.3-archive.zip",
            "4ac4847bbe4f7709b244956fcfc32197a2954ee70b155cb67eebd9ee26f7e339", 403672823, ["cublasLt64_13.dll", "cublas64_13.dll"]),
    ];

    /// <summary>読み込む順(依存される側を先に: cublas は cublasLt を要する)。</summary>
    public static IEnumerable<string> DllsInLoadOrder => Archives.SelectMany(a => a.Dlls);

    /// <summary>配布物・ビルド成果物に入っていてはならないファイル名(NVIDIA の著作物)。</summary>
    public static bool IsNvidiaDll(string fileName) =>
        DllsInLoadOrder.Any(d => string.Equals(d, Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase));

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voice2Txt", "runtime");
}

/// <summary>
/// CUDA の実行時ライブラリを runtime フォルダに用意する。zip を取得元からダウンロードし(途中の zip は download\ に残し、あれば先に使う)、
/// SHA-256 を照合してから必要な DLL だけを取り出す。取り出し終えた zip ごとに印(.verified-{名前})を置き、次からは取得しない。
/// </summary>
public sealed class CudaRuntimeProvisioner(string runtimeDir, HttpClient? http = null, IReadOnlyList<CudaArchive>? archives = null)
{
    private readonly IReadOnlyList<CudaArchive> _archives = archives ?? CudaRuntimeCatalog.Archives;
    public string RuntimeDir => runtimeDir;
    public string DownloadDir => Path.Combine(runtimeDir, "download");

    private string Marker(CudaArchive a) => Path.Combine(runtimeDir, ".verified-" + a.Name);

    public bool IsReady(CudaArchive a) =>
        File.Exists(Marker(a)) && a.Dlls.All(d => File.Exists(Path.Combine(runtimeDir, d)));

    /// <summary>全部の DLL が揃っている(取得しなくても読める)。</summary>
    public bool IsReady() => _archives.All(IsReady);

    /// <summary>読み込む DLL のフルパス(読み込む順)。</summary>
    public IEnumerable<string> DllPaths => _archives.SelectMany(a => a.Dlls).Select(d => Path.Combine(runtimeDir, d));

    /// <param name="progress">(段階, 0.0〜1.0)。段階は「ダウンロード」「検証」「展開」。</param>
    public async Task EnsureAsync(IProgress<(string Phase, double Ratio)>? progress, CancellationToken ct)
    {
        var todo = _archives.Where(a => !IsReady(a)).ToList();
        if (todo.Count == 0) return;
        Directory.CreateDirectory(DownloadDir);
        long total = todo.Sum(a => a.Size), before = 0;
        foreach (var a in todo)
        {
            long offset = before;
            var zip = Path.Combine(DownloadDir, a.Name + ".zip");
            if (File.Exists(zip))
            {
                progress?.Report(("検証", 0));
                var h = await ModelProvisioner.HashFileAsync(zip, new FileInfo(zip).Length, r => progress?.Report(("検証", r)), ct);
                if (h != a.Sha256) File.Delete(zip); // 壊れた・違う zip は取り直す
            }
            if (!File.Exists(zip))
                await DownloadAsync(a, zip, r => progress?.Report(("ダウンロード", total > 0 ? (offset + r * a.Size) / total : 0)), ct);
            progress?.Report(("展開", 0));
            Extract(a, zip);
            File.WriteAllText(Marker(a), a.Sha256);
            File.Delete(zip);
            before += a.Size;
        }
        try { if (!Directory.EnumerateFileSystemEntries(DownloadDir).Any()) Directory.Delete(DownloadDir); } catch (IOException) { }
    }

    private void Extract(CudaArchive a, string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        foreach (var dll in a.Dlls)
        {
            var entry = z.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), dll, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{a.Name} に {dll} が無い");
            var dst = Path.Combine(runtimeDir, dll);
            var tmp = dst + ".part";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
    }

    private async Task DownloadAsync(CudaArchive a, string zip, Action<double> onRatio, CancellationToken ct)
    {
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var part = zip + ".part";
        using var resp = await client.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long size = resp.Content.Headers.ContentLength ?? a.Size, done = 0;
        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                onRatio(size > 0 ? (double)done / size : 0);
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        if (Convert.ToHexStringLower(sha.Hash!) != a.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"CUDA の実行時ライブラリ({a.Name})のハッシュが一致しない");
        }
        File.Move(part, zip, overwrite: true);
    }
}
