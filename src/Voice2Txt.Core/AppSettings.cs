using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voice2Txt.Core;

/// <summary>設定(%APPDATA%\Voice2Txt\settings.json)。基盤の最小限: トークキーとモデル、言い淀みの除去。</summary>
public sealed class AppSettings
{
    /// <summary>トークキー。System.Windows.Forms.Keys の名前(例: RControlKey, RMenu)。</summary>
    public string TalkKey { get; set; } = "RControlKey";

    /// <summary>
    /// 録音のしかた(<see cref="RecordModes"/>): hold(初期値。トークキーを押している間だけ録音)/ toggle(押さない録音。<see cref="StartKey"/> で始め <see cref="StopKey"/> で止める。
    /// トークキーは録音に使わず、操作中のアプリへ素通しする)。
    /// </summary>
    public string RecordMode { get; set; } = RecordModes.Hold;

    /// <summary>押さない録音を始めるキーの組み合わせ(<see cref="KeyChord"/>。例 Ctrl+Alt+Space)。</summary>
    public string StartKey { get; set; } = KeyChord.DefaultStart;

    /// <summary>押さない録音を止めて文字起こしするキーの組み合わせ。開始キーと同じなら押すたびに始める・止めるが切り替わる。</summary>
    public string StopKey { get; set; } = KeyChord.DefaultStop;

    /// <summary>文字起こしのエンジン: whisper(初期値。無くても同じ)/ haas(Haas-ja-v1.0 を speech.cpp で。<see cref="Engines"/>)。</summary>
    public string Engine { get; set; } = Engines.Whisper;

    /// <summary>モデル名(ModelCatalog のキー。engine が whisper のとき)。</summary>
    public string Model { get; set; } = ModelCatalog.DefaultName;

    /// <summary>これより短い押下は取り消し(秒)。</summary>
    public double MinPressSeconds { get; set; } = 0.3;

    /// <summary>最も大きい 30 ms 区間の RMS がこれ未満なら無音として取り消し。実機で調整する。</summary>
    public double SilenceThreshold { get; set; } = 0.01;

    /// <summary>ログオン時の自動起動(初期値オフ)。</summary>
    public bool AutoStart { get; set; }

    /// <summary>確定版から言い淀み(<see cref="Fillers"/>)を取り除いてから届ける(初期値オン)。false で文字起こしのまま届ける。</summary>
    public bool RemoveFillers { get; set; } = true;

    /// <summary>取り除く言い淀みの語(かな。カタカナで書かれたものも同じ語として扱う)。2 文字の語は前後が区切りのときだけ取り除く。</summary>
    public List<string> Fillers { get; set; } = [.. Core.Fillers.DefaultWords];

    /// <summary>
    /// カスタム語彙(どちらのエンジンでも。haas は speech.cpp の keywords、whisper は表記を初期プロンプトに足す(<see cref="Decoding.PromptFor"/>))。
    /// 1 つの語は「表記」か「表記=読み,読み」(例 Haas=ハース。読みは haas だけが使う)。
    /// 空なら渡さない。読めない語は渡さずに知らせる(<see cref="HaasKeywords"/>)。
    /// </summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>押下中、それまでの音声の暫定の文字起こし(途中経過)をオーバーレイに出す(初期値オン)。操作中のアプリには書き込まない。</summary>
    public bool ShowInterim { get; set; } = true;

    /// <summary>CUDA の実行時ライブラリ(NVIDIA の cudart / cuBLAS)が無ければ、初回に NVIDIA の公式 redist から取得する(初期値オン)。false なら取得せず Vulkan か CPU で動く。</summary>
    public bool FetchCudaRuntime { get; set; } = true;

    /// <summary>文字起こしのバックエンド: auto(初期値。CUDA → Vulkan → CPU の順に読めるもの)/ cuda / vulkan / cpu(<see cref="Backends"/>)。</summary>
    public string Backend { get; set; } = Backends.Auto;

    /// <summary>
    /// 届け方(<see cref="Deliveries"/>): evacuate(初期値。離した時点の前面が押した時点と違えば貼らずにクリップボードへ退避)/
    /// return(押した時点のウィンドウを前面に戻して貼り、元の前面へ戻し、クリップボードを貼る前の中身に戻す)。
    /// </summary>
    public string Delivery { get; set; } = Deliveries.Evacuate;

    /// <summary>
    /// 速さを優先する(初期値オフ。engine が whisper のとき)。オンなら whisper のエンコーダが読む窓を 30 秒から発話の長さに縮め(<see cref="Decoding.AudioContext"/>)、
    /// CPU・弱い GPU で離してから届くまでを縮める。誤りが少し増えることがある。
    /// </summary>
    public bool PreferSpeed { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppVersion.ProductName);

    /// <summary>この PC だけの置き場(%LOCALAPPDATA%\Voice2Txt)。</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppVersion.ProductName);

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

    /// <summary>o の値をすべて写す(設定画面で保存した値を、動いている側の設定にも揃える)。</summary>
    public void CopyFrom(AppSettings o)
    {
        TalkKey = o.TalkKey; RecordMode = o.RecordMode; StartKey = o.StartKey; StopKey = o.StopKey; Engine = o.Engine; Model = o.Model; MinPressSeconds = o.MinPressSeconds; SilenceThreshold = o.SilenceThreshold;
        AutoStart = o.AutoStart; RemoveFillers = o.RemoveFillers; Fillers = [.. o.Fillers ?? []]; Keywords = [.. o.Keywords ?? []]; ShowInterim = o.ShowInterim;
        FetchCudaRuntime = o.FetchCudaRuntime; Backend = o.Backend; Delivery = o.Delivery; PreferSpeed = o.PreferSpeed;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
