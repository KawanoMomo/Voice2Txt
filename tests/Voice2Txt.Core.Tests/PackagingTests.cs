using System.Text.RegularExpressions;
using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>配布物を作る packaging/build.ps1 が、アプリが初回起動で取得する NVIDIA の DLL を必ず弾くこと(CudaRuntimeCatalog と食い違わない)。</summary>
public class PackagingTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Voice2Txt.sln"))) return d.FullName;
        throw new InvalidOperationException("Voice2Txt.sln が見つからない");
    }

    [Fact]
    public void 配布物の検査はCUDAの実行時ライブラリとモデルの重みを全部弾く()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "build.ps1"));
        var m = Regex.Match(script, @"\$_\.Name -match '(\^\(cudart[^']+)' -or \$_\.Name -match '([^']+)'");
        Assert.True(m.Success, "build.ps1 に NVIDIA の DLL とモデルの重みの検査が無い");
        var dll = new Regex(m.Groups[1].Value, RegexOptions.IgnoreCase);
        var data = new Regex(m.Groups[2].Value, RegexOptions.IgnoreCase);
        foreach (var name in CudaRuntimeCatalog.DllsInLoadOrder)
            Assert.Matches(dll, name);
        Assert.Matches(data, ModelCatalog.Get(ModelCatalog.DefaultName).FileName);
        // whisper.cpp の CUDA 版(MIT)は同梱してよい
        Assert.DoesNotMatch(dll, "ggml-cuda-whisper.dll");
        Assert.DoesNotMatch(dll, "whisper.dll");
        // Vulkan: whisper.cpp の Vulkan 版(MIT)は同梱してよく、ローダー(vulkan-1.dll)は利用者の GPU ドライバ側のものを使う
        Assert.Matches(dll, "vulkan-1.dll");
        Assert.DoesNotMatch(dll, "ggml-vulkan-whisper.dll");
    }

    [Fact]
    public void ワークフローはタグのpushで動き_setupとportableをReleaseに付ける()
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "windows-app.yml"));
        Assert.Contains("tags: ['v*']", yml);
        Assert.Contains("workflow_dispatch", yml);
        Assert.Contains("packaging/build.ps1", yml);
        Assert.Contains("dist/*-setup.exe", yml);
        Assert.Contains("dist/*-portable.zip", yml);
    }
}
