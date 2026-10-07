using System.Runtime.InteropServices;
using Voice2Txt.Core;

namespace Voice2Txt;

/// <summary>
/// 後入れした CUDA の実行時ライブラリ(runtime フォルダ)を、Whisper.net が CUDA 版を読む前にプロセスへ読み込んでおく。
/// Windows は同じ名前の DLL が読み込み済みならそれを使うので、ggml-cuda-whisper.dll の依存(cudart64_13 / cublas64_13 / cublasLt64_13)がここで解決する。
/// </summary>
internal static class CudaRuntimeLoader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? path);

    /// <summary>NVIDIA のドライバ(nvcuda.dll)が入っている PC か。入っていなければ取得しても CUDA では動かないので取得しない。</summary>
    public static bool HasNvidiaDriver()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var h)) return false;
        NativeLibrary.Free(h);
        return true;
    }

    /// <summary>runtime フォルダの DLL を読み込む。読めれば null、読めなければ理由(CPU で動く)。</summary>
    public static string? Preload(CudaRuntimeProvisioner prov)
    {
        if (!prov.IsReady()) return "CUDA の実行時ライブラリが無い";
        try
        {
            SetDllDirectory(prov.RuntimeDir);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(';').Contains(prov.RuntimeDir, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", prov.RuntimeDir + ";" + path);
            foreach (var dll in prov.DllPaths) NativeLibrary.Load(dll);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// 起動時の CUDA の用意: 無ければ(取得が許され、NVIDIA のドライバがあれば)取得してから読み込む。
    /// 戻り値は利用者に知らせる文(CUDA で動けるなら null、CPU で動くならその理由)。
    /// </summary>
    public static async Task<string?> PrepareAsync(CudaRuntimeProvisioner prov, bool fetch, Action<string> onProgress, CancellationToken ct)
    {
        if (!prov.IsReady())
        {
            if (!fetch) { AppLog.Write("cuda-runtime skipped fetch=false"); return "CUDA 無し(CPU): 取得しない設定です"; }
            if (!HasNvidiaDriver()) { AppLog.Write("cuda-runtime skipped no-driver"); return null; } // NVIDIA の GPU が無い PC は黙って CPU
            try
            {
                var last = "";
                AppLog.Write($"cuda-runtime fetch dir={prov.RuntimeDir}");
                await prov.EnsureAsync(new InlineProgress<(string Phase, double Ratio)>(p =>
                {
                    var s = $"CUDA の準備中 {p.Phase} {(int)(p.Ratio * 100)}%";
                    if (s != last) { last = s; onProgress(s); }
                }), ct);
                AppLog.Write("cuda-runtime ready");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Write("cuda-runtime-error " + ex.Message);
                return "CUDA 無し(CPU): CUDA の実行時ライブラリを取得できませんでした(" + ex.Message + ")";
            }
        }
        if (Preload(prov) is { } err) { AppLog.Write("cuda-runtime-error " + err); return "CUDA 無し(CPU): " + err; }
        AppLog.Write($"cuda-runtime loaded dir={prov.RuntimeDir}");
        return null;
    }
}
