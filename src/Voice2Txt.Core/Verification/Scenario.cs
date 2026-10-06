using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voice2Txt.Core.Verification;

/// <summary>検証モードの台本(tests/scenarios/{persona}-{手順}.json)。</summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";
    public string? Persona { get; set; }
    public int? Step { get; set; }
    public string? Description { get; set; }

    /// <summary>台本全体の上限(ms)。tools/Verify はこれを超えたら止めて failed にする。</summary>
    public int TimeoutMs { get; set; } = 300_000;

    /// <summary>既定の設定に重ねる値(talkKey / minPressSeconds / silenceThreshold / model)。</summary>
    public AppSettings? Settings { get; set; }

    /// <summary>モデルのフォルダ。省略時は %APPDATA%\Voice2Txt\models(全体で共用)。</summary>
    public string? ModelsDir { get; set; }

    public List<ScenarioAction> Actions { get; set; } = [];
    public Expectation Expect { get; set; } = new();

    public static Scenario Load(string path) =>
        JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), AppSettings.Json)
        ?? throw new InvalidDataException($"台本を読めない: {path}");

    /// <summary>台本の相対パスを解く: 台本のフォルダから上へ順に探す(リポジトリ直下からの相対も通る)。</summary>
    public static string ResolvePath(string scenarioPath, string p)
    {
        if (Path.IsPathRooted(p)) return p;
        var dir = Path.GetDirectoryName(Path.GetFullPath(scenarioPath));
        while (dir is not null)
        {
            var c = Path.Combine(dir, p);
            if (File.Exists(c) || Directory.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetFullPath(p);
    }
}

/// <summary>
/// 台本の 1 手。do は次のどれか:
/// waitModel(モデルの用意を待つ)/ press(トークキーを押す。audio に音声ファイル)/ holdUntilAudioEnd(流し終わるまで押し続ける)/
/// release(離す)/ key(押下中に別のキー key を押す)/ focus(前面を window = "textbox" か "other" に切り替える)/
/// wait(ms 待つ)/ waitIdle(処理待ちが 0 になるまで待つ)
/// </summary>
public sealed class ScenarioAction
{
    public string Do { get; set; } = "";
    public string? Audio { get; set; }
    public int? Ms { get; set; }
    public int? TimeoutMs { get; set; }
    public string? Key { get; set; }
    public string? Window { get; set; }
}

public sealed class Expectation
{
    /// <summary>届いた確定版(順序どおり)。</summary>
    public List<TextExpectation>? Deliveries { get; set; }

    /// <summary>検証用のテキスト欄の最終的な中身(確定版だけが入っていること)。</summary>
    public TextExpectation? Textbox { get; set; }

    /// <summary>オーバーレイの状態の列(この順に現れること。間に他の状態があってもよい)。</summary>
    public List<string>? States { get; set; }

    /// <summary>出てはならない状態。</summary>
    public List<string>? ForbiddenStates { get; set; }

    /// <summary>取り消しの理由の列(順序どおり。空配列なら取り消しが無いこと)。</summary>
    public List<string>? Cancellations { get; set; }

    /// <summary>離してから届くまでの上限(ms)。</summary>
    public long? MaxReleaseToDeliverMs { get; set; }

    /// <summary>読めていなければならない文字起こしのバックエンド(Cuda / Cpu)。</summary>
    public string? Runtime { get; set; }

    /// <summary>スクリーンショットが撮れていなければならない状態。</summary>
    public List<string>? Screenshots { get; set; }
}

public sealed class TextExpectation
{
    /// <summary>"textbox"(貼り付け)か "clipboard"(退避)。省略可。</summary>
    public string? To { get; set; }
    public string? Text { get; set; }

    /// <summary>句読点・空白を除いた文字単位の一致率の下限(0〜1)。省略時 1.0(完全一致)。</summary>
    public double? MinSimilarity { get; set; }

    [JsonIgnore] public double Threshold => MinSimilarity ?? 1.0;
}
