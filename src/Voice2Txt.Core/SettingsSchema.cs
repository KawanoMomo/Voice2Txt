using System.Globalization;
using System.Text.Json;

namespace Voice2Txt.Core;

public enum SettingKind { Choice, Number, Toggle, Words }

/// <summary>選択肢: 設定ファイルに書く値と、画面に出す名前。</summary>
public sealed record SettingChoice(string Value, string Label);

/// <summary>設定画面の 1 項目。Key は settings.json のキー名。</summary>
public sealed record SettingItem(
    string Key, string Label, string Description, SettingKind Kind,
    IReadOnlyList<SettingChoice>? Choices = null, double Min = 0, double Max = 0, double Step = 0, int Decimals = 0);

/// <summary>
/// 設定画面に並べる項目(名前・説明・選択肢・範囲)と、画面の値を設定へ移す規則。
/// 画面(トレイの「設定…」)と検証モードの台本(setSetting)が同じ規則を通る。保存先は settings.json(手書きも残す)。
/// </summary>
public static class SettingsSchema
{
    /// <summary>トークキーに選べるキー(単独で押しても他の入力と紛れにくいもの)。</summary>
    public static readonly IReadOnlyList<SettingChoice> TalkKeyChoices =
    [
        new("RControlKey", "右Ctrl"),
        new("RMenu", "右Alt"),
        new("RShiftKey", "右Shift"),
        new("LControlKey", "左Ctrl"),
        new("LMenu", "左Alt"),
        new("Apps", "アプリケーションキー"),
        new("Pause", "Pause"),
        new("Scroll", "ScrollLock"),
    ];

    /// <summary>トークキーの名前(Keys の名前)を画面に出す名前へ。選択肢に無ければそのまま。</summary>
    public static string TalkKeyLabel(string name) =>
        TalkKeyChoices.FirstOrDefault(c => string.Equals(c.Value, name, StringComparison.OrdinalIgnoreCase))?.Label ?? name;

    public static IReadOnlyList<SettingChoice> ModelChoices { get; } =
        ModelCatalog.Entries.Select(e => new SettingChoice(e.Name,
            $"{e.Name}({Size(e.Size)}{(e.Name == ModelCatalog.DefaultName ? "、既定" : "")})")).ToList();

    /// <summary>
    /// 片方のエンジンでだけ効く項目の示し方(1 つに揃える): 名前は用語のまま括弧を付けず、説明の先頭に「エンジンが … のときだけ使います。」と書く。
    /// </summary>
    public static string OnlyFor(string engine, string description) =>
        $"エンジンが {(Engines.IsHaas(engine) ? "Haas" : "Whisper")} のときだけ使います。{description}";

    public static IReadOnlyList<SettingItem> Items { get; } =
    [
        new("talkKey", "トークキー", "押している間だけ録音し、離すと文字起こしして貼り付けるキー。押している間に他のキーを押すと取り消しになり、そのキーとの組み合わせ(例 右Ctrl+C)として送られます。",
            SettingKind.Choice, TalkKeyChoices),
        new("recordMode", "録音のしかた", "押している間だけ録音(既定)か、押さない録音か。押さない録音は開始キーで録音を始め、終了キーで止めて貼り付けます。録音中に他のキーやマウスを使っても録音は途切れません(トークキーは録音に使わず、ふつうのキーとして働きます)。押している間だけ録音のままでも、開始キーを押せばその発話だけ押さない録音になり、終了キーで止めるとトークキーの録音に戻ります(切り替えに設定・再起動は要りません)。",
            SettingKind.Choice, RecordModes.Choices),
        new("startKey", "開始キー", "録音を始めるキーの組み合わせ(押さない録音でも、押している間だけ録音でトークキーを押していないときでも効きます)。設定ファイルには Ctrl / Alt / Shift / Win と 1 つのキーを + でつないで書けます(文字・数字・Space は Ctrl か Alt と組み合わせます)。",
            SettingKind.Choice, KeyChord.Choices),
        new("stopKey", "終了キー", "開始キーで始めた録音を止めて文字起こし・貼り付けをするキーの組み合わせ。開始キーと同じなら、押すたびに始める・止めるが切り替わります。",
            SettingKind.Choice, KeyChord.Choices),
        new("engine", "エンジン", "文字起こしのエンジン。Whisper(既定)か、日本語専用の Haas-ja-v1.0(speech.cpp で動かす。初回に speech.cpp と重み 0.66 GB を取得)。Haas を用意できないときは Whisper に切り替えず、理由をトレイに出します(トークキーを押せばオーバーレイにも理由と直し方が出ます)。",
            SettingKind.Choice, Engines.Choices),
        new("model", "モデル", OnlyFor(Engines.Whisper, "文字起こしに使うモデル。大きいほど正確で、用意(初回のダウンロード)と処理に時間がかかります。"),
            SettingKind.Choice, ModelChoices),
        new("backend", "バックエンド", "文字起こしに使う GPU / CPU。自動は CUDA(NVIDIA)→ Vulkan(GPU 全般)→ CPU の順に使えるものを選びます。選んだものを使えなければ CPU で動きます(CPU では途中経過を出しません)。",
            SettingKind.Choice, Backends.Choices),
        new("preferSpeed", "速さを優先する", OnlyFor(Engines.Whisper, "Whisper が 1 回に読む音声の窓(30 秒)を発話の長さに縮めて文字起こしします。CPU や弱い GPU で、離してから届くまでが大きく縮みます。誤りが少し増えることがあります(既定はオフ)。"),
            SettingKind.Toggle),
        new("delivery", "届け方", "離した時に前面のウィンドウが押した時と違うとき。既定は貼らずにクリップボードへ退避します(元の欄で Ctrl+V)。「押した時のウィンドウへ戻して貼る」は、押した時のウィンドウを一瞬前面に戻して貼り、元のウィンドウへ戻します。貼った後はクリップボードを貼る前の中身に戻します。",
            SettingKind.Choice, Deliveries.Choices),
        new("minPressSeconds", "短い押下の取り消し(秒)", "これより短くトークキーを押して離したときは、録音せずに取り消します(押し間違いの対策)。",
            SettingKind.Number, Min: 0.05, Max: 2.0, Step: 0.05, Decimals: 2),
        new("silenceThreshold", "無音のしきい値", "録音の最も大きい音がこれ未満で、雑音よりはっきり大きい声も続かなければ、無音として取り消します。小さい声が取り消されるときは下げます(0.001〜0.1)。",
            SettingKind.Number, Min: 0.001, Max: 0.1, Step: 0.001, Decimals: 3),
        new("showInterim", "途中経過を出す", "押している間、それまでの音声の暫定の文字起こしをオーバーレイにだけ出します(操作中のアプリには書き込みません)。",
            SettingKind.Toggle),
        new("removeFillers", "言い淀みを取り除く", "確定版から「ええ」「あの」「えっと」などの言い淀みを取り除いてから貼り付けます。",
            SettingKind.Toggle),
        new("fillers", "取り除く言い淀み", "取り除く語を読点(、)か空白で区切って並べます。2 文字の語は前後が句読点・文頭・文末のときだけ取り除きます(「あの人」は残ります)。文頭の「ああ、」は感嘆として残ります。",
            SettingKind.Words),
        new("keywords", "カスタム語彙", "固有名詞や、同音の別の表記で書かれてしまう語を、書いてほしい表記で読点(、)か改行で区切って並べます。その語が話されたときにこの表記で書かれやすくなります(どちらのエンジンでも効きます)。読みを添えるときは「表記=よみ,よみ」(例 Haas=ハース)。読みが効くのはエンジンが Haas のときで、Whisper は表記だけを使います。",
            SettingKind.Words),
        new("fetchCudaRuntime", "CUDA を取得する", OnlyFor(Engines.Whisper, "NVIDIA の GPU で速く動かすための実行時ライブラリ(cudart / cuBLAS)が無ければ、初回に NVIDIA の公式配布元から取得します。オフなら Vulkan か CPU で動きます。"),
            SettingKind.Toggle),
        new("autoStart", "ログオン時に起動する", "サインインしたときに自動で起動します(トレイのメニューと同じ)。",
            SettingKind.Toggle),
    ];

    public static SettingItem Get(string key) =>
        Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"設定の項目を読めない: {key}(項目: {string.Join(", ", Items.Select(i => i.Key))})");

    /// <summary>設定の今の値(設定ファイルに書く形の文字列。数は不変カルチャ、真偽は true/false、語は「、」区切り)。</summary>
    public static string Read(AppSettings s, string key) => Get(key).Key switch
    {
        "talkKey" => s.TalkKey,
        "recordMode" => s.RecordMode,
        "startKey" => s.StartKey,
        "stopKey" => s.StopKey,
        "engine" => s.Engine,
        "model" => s.Model,
        "backend" => s.Backend,
        "delivery" => s.Delivery,
        "minPressSeconds" => s.MinPressSeconds.ToString(CultureInfo.InvariantCulture),
        "silenceThreshold" => s.SilenceThreshold.ToString(CultureInfo.InvariantCulture),
        "showInterim" => s.ShowInterim ? "true" : "false",
        "preferSpeed" => s.PreferSpeed ? "true" : "false",
        "fetchCudaRuntime" => s.FetchCudaRuntime ? "true" : "false",
        "removeFillers" => s.RemoveFillers ? "true" : "false",
        "fillers" => string.Join("、", s.Fillers ?? []),
        "keywords" => string.Join("、", s.Keywords ?? []),
        "autoStart" => s.AutoStart ? "true" : "false",
        _ => throw new ArgumentException(key),
    };

    /// <summary>
    /// 画面の値を設定へ移す。値は設定ファイルに書く形でも画面に出す名前(「右Alt」「オン」)でもよい。
    /// 移せたら null、移せなければ利用者に見せる理由(設定は変えない)。
    /// </summary>
    public static string? Apply(AppSettings s, string key, string? value)
    {
        var item = Get(key);
        var v = (value ?? "").Trim();
        switch (item.Key)
        {
            case "talkKey":
            {
                var c = Match(TalkKeyChoices, v);
                if (c is not null) { s.TalkKey = c.Value; return null; }
                // 選択肢に無いキーでも、設定ファイルに手で書いた今の値はそのまま残せる
                if (v.Length > 0 && string.Equals(v, s.TalkKey, StringComparison.OrdinalIgnoreCase)) return null;
                return $"トークキー「{v}」は選べません(選べるキー: {string.Join("、", TalkKeyChoices.Select(x => x.Label))})";
            }
            case "recordMode":
            {
                if (RecordModes.Normalize(v) is { } rm && v.Length > 0) { s.RecordMode = rm; return null; }
                if (v.Length > 0 && string.Equals(v, s.RecordMode, StringComparison.OrdinalIgnoreCase)) return null;
                return $"録音のしかた「{v}」は選べません(選べるもの: {string.Join("、", RecordModes.Choices.Select(x => x.Label))})";
            }
            case "startKey":
            case "stopKey":
            {
                // 選択肢の名前(「Ctrl+Alt+Space(既定)」)か、読める組み合わせ(設定ファイルの形に揃えて書く)
                var c = Match(KeyChord.Choices, v);
                if (c is not null || KeyChord.TryParse(v, out _))
                {
                    KeyChord.TryParse(c?.Value ?? v, out var chord);
                    if (item.Key == "startKey") s.StartKey = chord.ToString(); else s.StopKey = chord.ToString();
                    return null;
                }
                var now = item.Key == "startKey" ? s.StartKey : s.StopKey;
                if (v.Length > 0 && string.Equals(v, now, StringComparison.OrdinalIgnoreCase)) return null;
                return $"{item.Label}「{v}」は読めません(例 Ctrl+Alt+Space。文字・数字・Space は Ctrl か Alt と組み合わせます)";
            }
            case "model":
            {
                var c = Match(ModelChoices, v);
                if (c is not null) { s.Model = c.Value; return null; }
                if (ModelCatalog.TryGet(v, out var e)) { s.Model = e.Name; return null; }
                return $"モデル「{v}」は選べません(選べるモデル: {ModelCatalog.Names})";
            }
            case "engine":
            {
                if (Engines.Normalize(v) is { } en && v.Length > 0) { s.Engine = en; return null; }
                if (v.Length > 0 && string.Equals(v, s.Engine, StringComparison.OrdinalIgnoreCase)) return null;
                return $"エンジン「{v}」は選べません(選べるもの: {string.Join("、", Engines.Choices.Select(x => x.Label))})";
            }
            case "backend":
            {
                if (Backends.Normalize(v) is { } b && v.Length > 0) { s.Backend = b; return null; }
                // 設定ファイルに手で書いた今の値はそのまま残せる(読めない値は起動時に自動として知らせる)
                if (v.Length > 0 && string.Equals(v, s.Backend, StringComparison.OrdinalIgnoreCase)) return null;
                return $"バックエンド「{v}」は選べません(選べるもの: {string.Join("、", Backends.Choices.Select(x => x.Label))})";
            }
            case "delivery":
            {
                if (Deliveries.Normalize(v) is { } dv && v.Length > 0) { s.Delivery = dv; return null; }
                if (v.Length > 0 && string.Equals(v, s.Delivery, StringComparison.OrdinalIgnoreCase)) return null;
                return $"届け方「{v}」は選べません(選べるもの: {string.Join("、", Deliveries.Choices.Select(x => x.Label))})";
            }
            case "minPressSeconds":
            case "silenceThreshold":
            {
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d))
                    return $"{item.Label}「{v}」は数ではありません";
                if (d < item.Min || d > item.Max)
                    return $"{item.Label}は {Fmt(item.Min)}〜{Fmt(item.Max)} の範囲で指定します(「{v}」)";
                if (item.Key == "minPressSeconds") s.MinPressSeconds = d; else s.SilenceThreshold = d;
                return null;
            }
            case "showInterim":
            case "preferSpeed":
            case "fetchCudaRuntime":
            case "removeFillers":
            case "autoStart":
            {
                bool? b = v.ToLowerInvariant() switch
                {
                    "true" or "on" or "オン" or "する" or "1" => true,
                    "false" or "off" or "オフ" or "しない" or "0" => false,
                    _ => null,
                };
                if (b is null) return $"{item.Label}は オン か オフ で指定します(「{v}」)";
                switch (item.Key)
                {
                    case "showInterim": s.ShowInterim = b.Value; break;
                    case "preferSpeed": s.PreferSpeed = b.Value; break;
                    case "fetchCudaRuntime": s.FetchCudaRuntime = b.Value; break;
                    case "removeFillers": s.RemoveFillers = b.Value; break;
                    default: s.AutoStart = b.Value; break;
                }
                return null;
            }
            case "fillers":
                s.Fillers = SplitWords(v);
                return null;
            case "keywords":
            {
                // 読みの区切りの「,」と語の中の空白(Third Intelligence など)は残す
                var words = HaasKeywords.Split(v);
                if (HaasKeywords.InvalidWarning(words) is { } bad) return bad;
                s.Keywords = words;
                return null;
            }
        }
        return $"設定の項目を読めない: {key}";
    }

    /// <summary>読点・カンマ・空白で区切った語の並び(空は除き、重複は 1 つに)。</summary>
    public static List<string> SplitWords(string v) =>
        v.Split(['、', ',', '，', '､', ' ', '　', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();

    /// <summary>設定の複製(画面で編集する間、元の設定を変えない)。</summary>
    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, AppSettings.Json), AppSettings.Json)!;

    /// <summary>a と b で値が違う項目のキー(保存したときに何が変わったかを知らせる・ログに残す)。</summary>
    public static List<string> Changed(AppSettings a, AppSettings b) =>
        Items.Where(i => Read(a, i.Key) != Read(b, i.Key)).Select(i => i.Key).ToList();

    private static SettingChoice? Match(IReadOnlyList<SettingChoice> cs, string v) =>
        cs.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c.Label, v, StringComparison.OrdinalIgnoreCase));

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static string Size(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";
}
