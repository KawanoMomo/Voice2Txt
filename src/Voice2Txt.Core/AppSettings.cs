using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voice2Txt.Core;

/// <summary>設定(%APPDATA%\Voice2Txt\settings.json)。基盤の最小限: トークキーとモデル。</summary>
public sealed class AppSettings
{
    /// <summary>トークキー。System.Windows.Forms.Keys の名前(例: RControlKey, RMenu)。</summary>
    public string TalkKey { get; set; } = "RControlKey";

    /// <summary>モデル名(ModelCatalog のキー)。</summary>
    public string Model { get; set; } = ModelCatalog.DefaultName;

    /// <summary>これより短い押下は取り消し(秒)。</summary>
    public double MinPressSeconds { get; set; } = 0.3;

    /// <summary>最も大きい 30 ms 区間の RMS がこれ未満なら無音として取り消し。実機で調整する。</summary>
    public double SilenceThreshold { get; set; } = 0.01;

    /// <summary>ログオン時の自動起動(初期値オフ)。</summary>
    public bool AutoStart { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Voice2Txt");

    /// <summary>読み込む。無ければ初期値で作って保存する。壊れていれば初期値を返し、壊れたファイルは .bad に残す。</summary>
    public static AppSettings LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                if (s is not null) return s;
            }
            catch (JsonException)
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }
        var d = new AppSettings();
        d.Save(path);
        return d;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
