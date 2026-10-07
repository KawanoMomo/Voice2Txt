namespace Voice2Txt.Core;

/// <summary>
/// 文字起こしのバックエンド(whisper.cpp の実行時ライブラリ)の選び方。設定 <see cref="AppSettings.Backend"/>:
/// auto(初期値)は CUDA → Vulkan → CPU の順に読めるものを使う。cuda / vulkan / cpu はそれを先に試し、読めなければ CPU で動く(起動しないよりよい)。
/// 名前は Whisper.net の RuntimeLibrary の名前(Cuda / Vulkan / Cpu)。結果の runtime とログ <c>engine-ready runtime=</c> も同じ名前。
/// </summary>
public static class Backends
{
    public const string Auto = "auto";
    public const string Cuda = "Cuda";
    public const string Vulkan = "Vulkan";
    public const string Cpu = "Cpu";

    /// <summary>設定に書ける値と画面に出す名前。</summary>
    public static readonly IReadOnlyList<SettingChoice> Choices =
    [
        new("auto", "自動(CUDA → Vulkan → CPU)"),
        new("cuda", "CUDA(NVIDIA)"),
        new("vulkan", "Vulkan(GPU 全般)"),
        new("cpu", "CPU"),
    ];

    /// <summary>設定の値を読む(大文字小文字・前後の空白は問わない)。読めなければ null。</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Auto;
        return Choices.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Label, v, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>試す順(読めない値は auto と同じ。呼び出し側は <see cref="InvalidWarning"/> を知らせる)。</summary>
    public static IReadOnlyList<string> Order(string? setting) => Normalize(setting) switch
    {
        "cuda" => [Cuda, Cpu],
        "vulkan" => [Vulkan, Cpu],
        "cpu" => [Cpu],
        _ => [Cuda, Vulkan, Cpu],
    };

    /// <summary>CUDA の実行時ライブラリ(NVIDIA の cudart / cuBLAS)を用意する必要があるか(CUDA を試す順のときだけ)。</summary>
    public static bool NeedsCudaRuntime(string? setting) => Order(setting).Contains(Cuda);

    /// <summary>backend を読めないときの知らせ(読めれば null)。</summary>
    public static string? InvalidWarning(string? setting) => Normalize(setting) is null
        ? $"設定の backend「{setting}」を読めないため 自動(CUDA → Vulkan → CPU)を使います"
        : null;

    /// <summary>CPU で動いているか(読めた名前で判断する。Cpu / CpuNoAvx)。</summary>
    public static bool IsCpu(string? runtime) => runtime is not null && runtime.StartsWith(Cpu, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 途中経過を出すか。CPU では 1 文に十数秒かかり、途中経過の作り直しが確定版を遅らせるので、設定がオンでも出さない(仕様 7 章)。
    /// まだ読めていない(null)ときは設定どおり。
    /// </summary>
    public static bool InterimAllowed(string? runtime, bool showInterimSetting) => showInterimSetting && !IsCpu(runtime);

    /// <summary>画面に出す名前(CUDA / Vulkan / CPU)。</summary>
    public static string Label(string? runtime) => runtime switch
    {
        null or "" => "不明",
        _ when runtime.Equals(Cuda, StringComparison.OrdinalIgnoreCase) => "CUDA",
        _ when runtime.Equals(Vulkan, StringComparison.OrdinalIgnoreCase) => "Vulkan",
        _ when IsCpu(runtime) => "CPU",
        _ => runtime,
    };

    /// <summary>待機中のトレイのツールチップの本文(常駐時と検証モードで同じ): 「待機中(モデル large-v3-turbo、CUDA)」。</summary>
    public static string IdleTrayStatus(string model, string? runtime) => $"待機中(モデル {model}、{Label(runtime)})";

    /// <summary>
    /// 設定で選んだものと違うバックエンドで動いたときに知らせる文(同じなら null)。auto で CPU に落ちたときも知らせる(遅いことが分かるように)。
    /// </summary>
    public static string? FallbackNote(string? setting, string? runtime)
    {
        var first = Order(setting)[0];
        if (runtime is not null && runtime.Equals(first, StringComparison.OrdinalIgnoreCase)) return null;
        if (Normalize(setting) == Auto && !IsCpu(runtime)) return null; // auto で GPU のどれかが読めた
        return $"{Label(runtime)} で動いています({(Normalize(setting) == Auto ? "GPU を使えません" : $"{Label(first)} を使えません")})。"
             + (IsCpu(runtime) ? "1 文に十数秒かかり、途中経過は出しません" : "");
    }
}
