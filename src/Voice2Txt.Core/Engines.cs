namespace Voice2Txt.Core;

/// <summary>
/// 文字起こしのエンジンの選び方。設定 <see cref="AppSettings.Engine"/>: whisper(初期値。whisper.cpp をプロセス内に取り込む)/
/// haas(日本語 ASR の Haas-ja-v1.0 を speech.cpp の worker で動かす)。haas を用意できないときは Whisper に黙って落とさず、理由を知らせる。
/// </summary>
public static class Engines
{
    public const string Whisper = "whisper";
    public const string Haas = "haas";

    /// <summary>設定に書ける値と画面に出す名前。</summary>
    public static readonly IReadOnlyList<SettingChoice> Choices =
    [
        new(Whisper, "Whisper(whisper.cpp、既定)"),
        new(Haas, "Haas-ja-v1.0(speech.cpp、日本語)"),
    ];

    /// <summary>設定の値を読む(大文字小文字・前後の空白は問わない。空・無しは whisper)。読めなければ null。</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Whisper;
        return Choices.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Label, v, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>haas を使う設定か(読めない値は whisper。呼び出し側は <see cref="InvalidWarning"/> を知らせる)。</summary>
    public static bool IsHaas(string? setting) => Normalize(setting) == Haas;

    /// <summary>engine を読めないときの知らせ(読めれば null)。</summary>
    public static string? InvalidWarning(string? setting) => Normalize(setting) is null
        ? $"設定の engine「{setting}」を読めないため Whisper を使います(選べる値: {string.Join(", ", Choices.Select(c => c.Value))})"
        : null;

    /// <summary>使うモデルの名前(haas は Haas-ja-v1.0 の重み、whisper は設定 model)。</summary>
    public static string ModelName(AppSettings s) => IsHaas(s.Engine) ? SpeechCatalog.HaasModel.Name : ModelCatalog.Get(s.Model).Name;

    /// <summary>
    /// 待機中のトレイのツールチップの本文(常駐時と検証モードで同じ)。whisper は今までどおり「待機中(モデル large-v3-turbo、CUDA)」、
    /// haas は「待機中(エンジン haas、モデル haas-ja-v1.0、Vulkan)」。
    /// </summary>
    public static string IdleTrayStatus(string? engine, string model, string? runtime) => IsHaas(engine)
        ? $"待機中(エンジン {Haas}、モデル {model}、{Backends.Label(runtime)})"
        : Backends.IdleTrayStatus(model, runtime);

    /// <summary>エンジンを用意できなかったときのトレイのツールチップの本文。</summary>
    public static string FailedTrayStatus(string? engine) => IsHaas(engine) ? "Haas エンジンを用意できません" : "モデルを用意できません";
}

/// <summary>エンジンを用意できない理由(利用者に見せる文。トレイの通知・ログ・検証モードの結果に出す)。</summary>
public sealed class EngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
