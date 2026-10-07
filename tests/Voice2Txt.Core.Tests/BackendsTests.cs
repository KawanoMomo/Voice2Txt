using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>バックエンドの選び方(設定 backend)・CPU での途中経過の自動オフ・トレイの表示。</summary>
public class BackendsTests
{
    [Theory]
    [InlineData("auto", "Cuda,Vulkan,Cpu")]
    [InlineData(null, "Cuda,Vulkan,Cpu")]
    [InlineData("", "Cuda,Vulkan,Cpu")]
    [InlineData("cuda", "Cuda,Cpu")]
    [InlineData("VULKAN", "Vulkan,Cpu")]
    [InlineData(" vulkan ", "Vulkan,Cpu")]
    [InlineData("cpu", "Cpu")]
    [InlineData("opencl", "Cuda,Vulkan,Cpu")]
    public void 試す順は設定で決まり_読めない値は自動と同じ(string? setting, string order) =>
        Assert.Equal(order, string.Join(",", Backends.Order(setting)));

    [Fact]
    public void 読めない値だけ警告する()
    {
        Assert.Null(Backends.InvalidWarning("auto"));
        Assert.Null(Backends.InvalidWarning("Vulkan"));
        Assert.Contains("opencl", Backends.InvalidWarning("opencl"));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("cuda", true)]
    [InlineData("vulkan", false)]
    [InlineData("cpu", false)]
    public void CUDAの実行時ライブラリはCUDAを試す設定のときだけ用意する(string setting, bool needs) =>
        Assert.Equal(needs, Backends.NeedsCudaRuntime(setting));

    [Theory]
    [InlineData("Cuda", true, true)]
    [InlineData("Vulkan", true, true)]
    [InlineData("Cpu", true, false)]
    [InlineData("CpuNoAvx", true, false)]
    [InlineData("Vulkan", false, false)]
    [InlineData(null, true, true)]
    public void CPUでは途中経過を出さない(string? runtime, bool setting, bool allowed) =>
        Assert.Equal(allowed, Backends.InterimAllowed(runtime, setting));

    [Theory]
    [InlineData("Cuda", "待機中(モデル large-v3-turbo、CUDA)")]
    [InlineData("Vulkan", "待機中(モデル large-v3-turbo、Vulkan)")]
    [InlineData("Cpu", "待機中(モデル large-v3-turbo、CPU)")]
    public void トレイの待機中にバックエンドを出す(string runtime, string text) =>
        Assert.Equal(text, Backends.IdleTrayStatus("large-v3-turbo", runtime));

    [Fact]
    public void 待機中のツールチップは上限に収まる() =>
        Assert.True(AppVersion.TrayText(Backends.IdleTrayStatus("large-v3-turbo", "Vulkan")).Length < 63);

    [Theory]
    [InlineData("auto", "Cuda", false)]
    [InlineData("auto", "Vulkan", false)]
    [InlineData("auto", "Cpu", true)]
    [InlineData("vulkan", "Vulkan", false)]
    [InlineData("vulkan", "Cpu", true)]
    [InlineData("cuda", "Vulkan", true)]
    [InlineData("cpu", "Cpu", false)]
    public void 選んだものと違うバックエンドやCPUに落ちたときだけ知らせる(string setting, string runtime, bool notify)
    {
        var note = Backends.FallbackNote(setting, runtime);
        Assert.Equal(notify, note is not null);
        if (notify && runtime == "Cpu") Assert.Contains("途中経過は出しません", note);
    }

    [Fact]
    public void 設定画面でバックエンドを選べ_画面の名前でも保存できる()
    {
        var s = new AppSettings();
        Assert.Equal("auto", SettingsSchema.Read(s, "backend"));
        Assert.Null(SettingsSchema.Apply(s, "backend", "Vulkan(GPU 全般)"));
        Assert.Equal("vulkan", s.Backend);
        Assert.Null(SettingsSchema.Apply(s, "backend", "CPU"));
        Assert.Equal("cpu", s.Backend);
        Assert.NotNull(SettingsSchema.Apply(s, "backend", "opencl"));
        Assert.Equal("cpu", s.Backend);
        var t = new AppSettings();
        t.CopyFrom(s);
        Assert.Equal("cpu", t.Backend);
        Assert.Equal(["backend"], SettingsSchema.Changed(new AppSettings(), s));
    }

    [Fact]
    public void 設定ファイルの初期値は自動()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettings.Json);
        Assert.Contains("\"backend\": \"auto\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"backend\":\"vulkan\"}", AppSettings.Json)!;
        Assert.Equal(["Vulkan", "Cpu"], Backends.Order(back.Backend));
    }
}
