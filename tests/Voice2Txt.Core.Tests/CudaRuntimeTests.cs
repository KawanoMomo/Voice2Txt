using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class CudaRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v2t-cuda-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>redist と同じ形の zip(名前/bin/x64/*.dll)。</summary>
    private static byte[] Zip(string name, params string[] dlls)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var d in dlls.Append("nvblas64_13.dll"))
            {
                using var w = new StreamWriter(z.CreateEntry($"{name}/bin/x64/{d}").Open());
                w.Write("dll:" + d);
            }
            z.CreateEntry($"{name}/LICENSE.txt");
        }
        return ms.ToArray();
    }

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(files.TryGetValue(req.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (CudaArchive[] Archives, Dictionary<string, byte[]> Files) Fixture(bool badHash = false)
    {
        var z1 = Zip("rt", "cudart64_13.dll");
        var z2 = Zip("blas", "cublasLt64_13.dll", "cublas64_13.dll");
        string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
        var a = new[]
        {
            new CudaArchive("rt", "https://example.invalid/rt.zip", H(z1), z1.Length, ["cudart64_13.dll"]),
            new CudaArchive("blas", "https://example.invalid/blas.zip", badHash ? new string('0', 64) : H(z2), z2.Length, ["cublasLt64_13.dll", "cublas64_13.dll"]),
        };
        return (a, new() { [a[0].Url] = z1, [a[1].Url] = z2 });
    }

    [Fact]
    public async Task 無ければ取得してハッシュを照合し_必要なDLLだけを取り出す_次からは取得しない()
    {
        var (archives, files) = Fixture();
        var server = new Server(files);
        var p = new CudaRuntimeProvisioner(_dir, new HttpClient(server), archives);
        Assert.False(p.IsReady());
        var phases = new List<string>();
        await p.EnsureAsync(new SyncProgress(x => phases.Add(x.Phase)), CancellationToken.None);
        Assert.True(p.IsReady());
        Assert.Equal(["cudart64_13.dll", "cublasLt64_13.dll", "cublas64_13.dll"], p.DllPaths.Select(Path.GetFileName));
        Assert.All(p.DllPaths, f => Assert.Equal("dll:" + Path.GetFileName(f), File.ReadAllText(f)));
        Assert.False(File.Exists(Path.Combine(_dir, "nvblas64_13.dll"))); // 要らないものは取り出さない
        Assert.False(Directory.Exists(p.DownloadDir)); // zip は残さない
        Assert.Contains("ダウンロード", phases);
        Assert.Equal(2, server.Calls);
        await p.EnsureAsync(null, CancellationToken.None);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task ハッシュが合わなければ置かずに失敗し_取り出さない()
    {
        var (archives, files) = Fixture(badHash: true);
        var p = new CudaRuntimeProvisioner(_dir, new HttpClient(new Server(files)), archives);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => p.EnsureAsync(null, CancellationToken.None));
        Assert.Contains("ハッシュ", ex.Message);
        Assert.False(p.IsReady());
        Assert.False(File.Exists(Path.Combine(_dir, "cublas64_13.dll")));
        Assert.True(p.IsReady(archives[0])); // 合っていた方は使える
    }

    [Fact]
    public async Task download_に取得元と同じzipがあれば_ネットに出ずにそれを使う()
    {
        var (archives, files) = Fixture();
        var server = new Server(files);
        var p = new CudaRuntimeProvisioner(_dir, new HttpClient(server), archives);
        Directory.CreateDirectory(p.DownloadDir);
        File.WriteAllBytes(Path.Combine(p.DownloadDir, "rt.zip"), files[archives[0].Url]);
        File.WriteAllBytes(Path.Combine(p.DownloadDir, "blas.zip"), files[archives[1].Url]);
        await p.EnsureAsync(null, CancellationToken.None);
        Assert.True(p.IsReady());
        Assert.Equal(0, server.Calls);
    }

    [Fact]
    public async Task download_の壊れたzipは取り直す()
    {
        var (archives, files) = Fixture();
        var server = new Server(files);
        var p = new CudaRuntimeProvisioner(_dir, new HttpClient(server), archives);
        Directory.CreateDirectory(p.DownloadDir);
        File.WriteAllBytes(Path.Combine(p.DownloadDir, "rt.zip"), [1, 2, 3]);
        await p.EnsureAsync(null, CancellationToken.None);
        Assert.True(p.IsReady());
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public void 取得元はNVIDIAの公式redistに固定し_読む順はcublasLtがcublasより先()
    {
        Assert.All(CudaRuntimeCatalog.Archives, a =>
        {
            Assert.StartsWith("https://developer.download.nvidia.com/compute/cuda/redist/", a.Url);
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
        });
        Assert.Equal(["cudart64_13.dll", "cublasLt64_13.dll", "cublas64_13.dll"], CudaRuntimeCatalog.DllsInLoadOrder);
        Assert.True(CudaRuntimeCatalog.IsNvidiaDll(@"C:\x\cublasLt64_13.dll"));
        Assert.False(CudaRuntimeCatalog.IsNvidiaDll("ggml-cuda-whisper.dll"));
    }

    [Fact]
    public void 取得は設定で止められる_初期値は取得する()
    {
        Assert.True(new AppSettings().FetchCudaRuntime);
        var s = new AppSettings();
        Assert.Null(SettingsSchema.Apply(s, "fetchCudaRuntime", "オフ"));
        Assert.False(s.FetchCudaRuntime);
    }

    private sealed class SyncProgress(Action<(string Phase, double Ratio)> f) : IProgress<(string Phase, double Ratio)>
    {
        public void Report((string Phase, double Ratio) v) { lock (this) f(v); }
    }
}
