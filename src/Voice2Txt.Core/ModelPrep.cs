using System.Text.RegularExpressions;

namespace Voice2Txt.Core;

/// <summary>
/// モデルの準備中に出す文(トレイのツールチップ・オーバーレイの「モデル準備中(…)」)の段階と進み具合。
/// 準備の文は「ダウンロード 30%」「検証 70%」「読み込み」「暖機」「CUDA の準備中 ダウンロード 5%」の形。
/// </summary>
public static partial class ModelPrepProgress
{
    /// <summary>記録の刻み(%)。画面は 1% ごとに変わるが、記録・スクショはこの刻みを跨いだときと段階が変わったときだけにする。</summary>
    public const int StepPercent = 10;

    /// <summary>トレイのツールチップに出す状態(常駐時と検証モードで同じ文言)。</summary>
    public static string TrayStatus(string? detail) => detail is null ? "モデル準備中" : $"モデル準備中 {detail}";

    /// <summary>準備の文を段階と進み具合(%)に分ける。% が無ければ進み具合は null。</summary>
    public static (string Stage, int? Percent) Parse(string detail)
    {
        var m = PercentRx().Match(detail);
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (detail, null);
    }

    /// <summary>記録の区切り: 段階が同じで、進み具合が同じ刻みの中にある文は同じ区切り。</summary>
    public static string Milestone(string? detail)
    {
        if (detail is null) return "";
        var (stage, pct) = Parse(detail);
        return pct is { } p ? $"{stage}|{Math.Clamp(p, 0, 100) / StepPercent}" : stage;
    }

    [GeneratedRegex(@"^(.*?)\s*(\d{1,3})%$")]
    private static partial Regex PercentRx();
}

/// <summary>
/// 報告をその場で渡す <see cref="IProgress{T}"/>。<see cref="Progress{T}"/> は報告を後から UI スレッドへ投げるので、
/// 取得の最後の「ダウンロード 100%」が次の段階「読み込み」の後に届き、準備中の表示が前の段階に戻ってしまう。
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>準備の文の列から、記録する区切り(段階が変わった・刻みを跨いだ)だけを通す。</summary>
public sealed class ModelPrepTracker
{
    private string? _last;

    /// <summary>前に通した文と区切りが違えば true(記録する)。</summary>
    public bool Accept(string? detail)
    {
        var k = ModelPrepProgress.Milestone(detail);
        if (k == _last) return false;
        _last = k;
        return true;
    }

    public void Reset() => _last = null;
}
