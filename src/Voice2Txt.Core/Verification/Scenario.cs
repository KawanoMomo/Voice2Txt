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
/// waitModel(モデルの用意を待つ)/ press(キー key を押す。省略時は設定のトークキー。audio に音声ファイル)/ holdUntilAudioEnd(流し終わるまで押し続ける)/
/// release(キー key を離す。省略時はトークキー)/ key(キー key を押して離す。省略時 C)/ focus(前面を window = "textbox" か "other" に切り替える)/
/// wait(ms 待つ)/ waitIdle(処理待ちが 0 になるまで待つ)/ lockClipboard(今から ms の間、他アプリが開いたままのようにクリップボードを使えなくする)/
/// shot(今のオーバーレイを name の名で撮り、そのときの状態と音量バーの値を結果の shots に残す)
/// </summary>
public sealed class ScenarioAction
{
    public string Do { get; set; } = "";
    public string? Audio { get; set; }
    public int? Ms { get; set; }
    public int? TimeoutMs { get; set; }
    /// <summary>キーの名前(System.Windows.Forms.Keys の名前。例 RControlKey, RMenu, C)。キーはトークキーの判定に通す。</summary>
    public string? Key { get; set; }
    public string? Window { get; set; }
    /// <summary>shot の名前(期待の shots と結び付ける)。</summary>
    public string? Name { get; set; }
}

public sealed class Expectation
{
    /// <summary>届いた確定版(順序どおり)。</summary>
    public List<TextExpectation>? Deliveries { get; set; }

    /// <summary>検証用のテキスト欄の最終的な中身(確定版だけが入っていること)。</summary>
    public TextExpectation? Textbox { get; set; }

    /// <summary>オーバーレイの状態の列(この順に現れること。間に他の状態があってもよい)。</summary>
    public List<string>? States { get; set; }

    /// <summary>オーバーレイの文言に現れていなければならない文字列(どれかの状態の本文に含まれること)。</summary>
    public List<string>? StateTexts { get; set; }

    /// <summary>出てはならない状態。</summary>
    public List<string>? ForbiddenStates { get; set; }

    /// <summary>取り消しの理由の列(順序どおり。空配列なら取り消しが無いこと)。</summary>
    public List<string>? Cancellations { get; set; }

    /// <summary>離してから届くまでの上限(ms)。</summary>
    public long? MaxReleaseToDeliverMs { get; set; }

    /// <summary>読めていなければならない文字起こしのバックエンド(Cuda / Cpu)。</summary>
    public string? Runtime { get; set; }

    /// <summary>読み込めていなければならないモデルの名前(ModelCatalog の名前)。</summary>
    public string? Model { get; set; }

    /// <summary>操作中のアプリへ素通ししたキーの列("RControlKey down" の形。順序どおり。空配列なら素通し無し)。</summary>
    public List<string>? PassedKeys { get; set; }

    /// <summary>押下中の別キーで合成して送ったキーの列("RControlKey down", "C down")。</summary>
    public List<string>? SentKeys { get; set; }

    /// <summary>警告の列(空配列なら警告が無いこと)。</summary>
    public List<string>? Warnings { get; set; }

    /// <summary>スクリーンショットが撮れていなければならない状態。</summary>
    public List<string>? Screenshots { get; set; }

    /// <summary>台本の shot で撮ったもの(名前で引く。状態と音量バーの値の範囲)。</summary>
    public List<ShotExpectation>? Shots { get; set; }
}

public sealed class ShotExpectation
{
    public string Name { get; set; } = "";
    /// <summary>撮ったときの状態(省略可)。</summary>
    public string? State { get; set; }
    /// <summary>音量バーの値(0〜1)の下限・上限(省略可)。</summary>
    public double? MinMeter { get; set; }
    public double? MaxMeter { get; set; }
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
