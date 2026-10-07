using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Voice2Txt.Core.Verification;

/// <summary>検証モードが <c>--out</c> に書く result.json。</summary>
public sealed class VerifyResult
{
    public string Scenario { get; set; } = "";
    /// <summary>動いた exe の版(タグの形 v{major}.{minor})。</summary>
    public string? Version { get; set; }
    /// <summary>常駐時ならトレイアイコンのツールチップに出している文言(最後の状態。版を含む)。</summary>
    public string? TrayTooltip { get; set; }
    public bool Completed { get; set; }
    public string? Error { get; set; }
    /// <summary>読めた文字起こしのバックエンド(Cuda / Vulkan / Cpu)。</summary>
    public string? Runtime { get; set; }
    /// <summary>設定 backend(auto / cuda / vulkan / cpu)。</summary>
    public string? Backend { get; set; }
    /// <summary>途中経過を出していたか(設定 showInterim がオンでも、CPU で動くときは出さない)。</summary>
    public bool? Interim { get; set; }
    /// <summary>読み込んだモデルの名前(台本の settings.model を引いた結果)。</summary>
    public string? Model { get; set; }
    public long? ModelReadyMs { get; set; }
    /// <summary>CUDA の実行時ライブラリ: ready(runtime フォルダから読めた)か、CUDA を使えない理由。検証モードは取得しない。</summary>
    public string? CudaRuntime { get; set; }
    /// <summary>モデルの準備の段階の列(段階が変わるたびと、進み具合が 10% の刻みを跨ぐたび。そのときのトレイのツールチップ付き)。</summary>
    public List<ModelPrepRecord> ModelPrep { get; set; } = [];
    public List<DeliveryRecord> Deliveries { get; set; } = [];
    public List<CancelRecord> Cancellations { get; set; } = [];
    public List<StateRecord> States { get; set; } = [];
    /// <summary>台本の shot で撮ったもの(撮った順)。</summary>
    public List<ShotRecord> Shots { get; set; } = [];
    public string Textbox { get; set; } = "";
    public string? Clipboard { get; set; }

    /// <summary>トークキーの判定で操作中のアプリへ素通ししたキー("RControlKey down" の形。順に)。</summary>
    public List<string> PassedKeys { get; set; } = [];

    /// <summary>押下中の別キーで、トークキーを修飾キーとして合成して送ったキー列("RControlKey down", "C down")。</summary>
    public List<string> SentKeys { get; set; } = [];

    /// <summary>設定の問題など、利用者に知らせた警告(例: talkKey を読めない)。</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>設定画面で最後に保存した設定(保存していなければ null)と、そのとき変わった項目のキー。</summary>
    public AppSettings? SavedSettings { get; set; }
    public List<string>? SettingsChanged { get; set; }

    /// <summary>設定画面で保存できなかった理由(画面に出した文)。</summary>
    public List<string> SettingsErrors { get; set; } = [];

    /// <summary>台本の restart で設定を読み直した回数。</summary>
    public int Restarts { get; set; }

    /// <summary>本物の前面ウィンドウ(GetForegroundWindow)を調べた回数(台本の実行中、一定間隔と状態が変わるたび)。</summary>
    public int ForegroundSamples { get; set; }

    /// <summary>そのうちオーバーレイ自身が前面だった回数(0 でなければフォーカスを奪っている)。</summary>
    public int OverlayForegroundCount { get; set; }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, AppSettings.Json));
    public static VerifyResult Load(string path) =>
        JsonSerializer.Deserialize<VerifyResult>(File.ReadAllText(path), AppSettings.Json) ?? new();
}

public sealed class DeliveryRecord
{
    public int Seq { get; set; }
    public string To { get; set; } = "";  // textbox / clipboard
    public string Text { get; set; } = "";
    public long? MicOpenMs { get; set; }
    public long HeldMs { get; set; }
    public long? ReleaseToDeliverMs { get; set; }
    public long? TranscribeMs { get; set; }
    /// <summary>届ける前に確定版から取り除いた言い淀みの数(設定 removeFillers)。</summary>
    public int FillersRemoved { get; set; }
}

/// <summary>モデルの準備の 1 段階(例 Stage = ダウンロード, Percent = 30)。</summary>
public sealed class ModelPrepRecord
{
    public long AtMs { get; set; }
    public string Stage { get; set; } = "";
    public int? Percent { get; set; }
    /// <summary>そのときトレイのツールチップに出していた文言(常駐時と同じ)。</summary>
    public string Tooltip { get; set; } = "";
}

public sealed class CancelRecord
{
    public int Seq { get; set; }
    public string Reason { get; set; } = "";
    public long HeldMs { get; set; }
    public string? Error { get; set; }
}

public sealed class StateRecord
{
    public long AtMs { get; set; }
    public string State { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>この状態で出していた途中経過の文字数(本文は書かない)。</summary>
    public int InterimChars { get; set; }
    public string? Screenshot { get; set; }
    public string? Capture { get; set; } // screen / render

    /// <summary>この状態を出した直後の本物の前面ウィンドウ: overlay / textbox / other。</summary>
    public string? Foreground { get; set; }
}

/// <summary>台本の shot で撮ったオーバーレイ。</summary>
public sealed class ShotRecord
{
    public string Name { get; set; } = "";
    public long AtMs { get; set; }
    public string State { get; set; } = "";
    /// <summary>撮ったときに音量バーへ描いていた値(0〜1)。</summary>
    public double Meter { get; set; }
    /// <summary>撮ったときにオーバーレイへ描いていた途中経過の文字数(本文は書かない)。</summary>
    public int InterimChars { get; set; }
    /// <summary>撮ったときの途中経過の行数(無ければ 0)。</summary>
    public int InterimLines { get; set; }
    /// <summary>常に出る案内の行(状態の印と本文)の、画面上の左上(px)。</summary>
    public int HintRowX { get; set; }
    public int HintRowY { get; set; }
    public string? Screenshot { get; set; }
    public string? Capture { get; set; }
}

public static class TextSimilarity
{
    /// <summary>NFKC にし、空白・句読点・記号を除く(日本語の助詞以外の揺れを吸収する)。</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || cat == UnicodeCategory.Format) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>1 - 編集距離 / 長い方の長さ(正規化後)。</summary>
    public static double Ratio(string expected, string actual)
    {
        var a = Normalize(expected); var b = Normalize(actual);
        if (a.Length == 0 && b.Length == 0) return 1;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}

/// <summary>result.json を台本の期待と比べる(tools/Verify が使う)。</summary>
public static class Evaluator
{
    public static List<string> Check(Expectation e, VerifyResult r, Func<string, bool>? screenshotExists = null)
    {
        var fails = new List<string>();
        if (!r.Completed) fails.Add($"台本を最後まで実行できなかった: {r.Error}");
        // 受け入れ基準: オーバーレイはフォーカスを奪わない(期待に書かなくても検査する)
        if (r.OverlayForegroundCount > 0)
        {
            var at = r.States.Where(s => s.Foreground == "overlay").Select(s => s.State).ToList();
            fails.Add($"オーバーレイが前面(フォーカス)を奪った({r.OverlayForegroundCount}/{r.ForegroundSamples} 回{(at.Count > 0 ? "。状態: " + string.Join(",", at) : "")})");
        }

        if (e.Deliveries is { } ds)
        {
            if (ds.Count != r.Deliveries.Count) fails.Add($"届いた数 {r.Deliveries.Count}(期待 {ds.Count})");
            for (int i = 0; i < Math.Min(ds.Count, r.Deliveries.Count); i++)
            {
                var x = ds[i]; var got = r.Deliveries[i];
                if (x.To is not null && x.To != got.To) fails.Add($"{i + 1} 件目の届け先 {got.To}(期待 {x.To})");
                if (x.Text is not null)
                {
                    var ratio = TextSimilarity.Ratio(x.Text, got.Text);
                    if (ratio < x.Threshold) fails.Add($"{i + 1} 件目の文字列の一致率 {ratio:0.00}(下限 {x.Threshold:0.00})");
                }
                foreach (var w in Absent(x, got.Text)) fails.Add($"{i + 1} 件目に「{w}」が残っている");
                foreach (var w in Missing(x, got.Text)) fails.Add($"{i + 1} 件目に「{w}」が無い");
            }
            for (int i = 1; i < r.Deliveries.Count; i++)
                if (r.Deliveries[i].Seq < r.Deliveries[i - 1].Seq) fails.Add("届いた順が録音した順と違う");
        }
        if (e.Textbox is { Text: not null } tb)
        {
            var ratio = TextSimilarity.Ratio(tb.Text, r.Textbox);
            if (ratio < tb.Threshold) fails.Add($"テキスト欄の一致率 {ratio:0.00}(下限 {tb.Threshold:0.00})");
        }
        if (e.Textbox is { } tb2)
            foreach (var w in Absent(tb2, r.Textbox)) fails.Add($"テキスト欄に「{w}」が残っている");
        if (e.States is { } st)
        {
            var seen = r.States.Select(s => s.State).ToList();
            int k = 0;
            foreach (var s in seen) if (k < st.Count && s == st[k]) k++;
            if (k < st.Count) fails.Add($"状態「{st[k]}」が期待の順に現れない(実際: {string.Join(" → ", seen)})");
        }
        if (e.ModelPrep is { } mp)
        {
            var seen = r.ModelPrep.Select(s => s.Stage).ToList();
            int k = 0;
            foreach (var s in seen) if (k < mp.Count && s == mp[k]) k++;
            if (k < mp.Count) fails.Add($"モデルの準備の段階「{mp[k]}」が期待の順に記録されていない(実際: {string.Join(" → ", seen.Distinct())})");
        }
        if (e.StateTexts is { } txts)
            foreach (var t in txts.Where(t => !r.States.Any(x => x.Text.Contains(t))))
                fails.Add($"オーバーレイに「{t}」が出ていない");
        if (e.ForbiddenStates is { } fs)
            foreach (var s in fs.Where(f => r.States.Any(x => x.State == f))) fails.Add($"出てはならない状態「{s}」が出た");
        if (e.Cancellations is { } cs)
        {
            var got = r.Cancellations.Select(c => c.Reason).ToList();
            if (!got.SequenceEqual(cs)) fails.Add($"取り消し [{string.Join(",", got)}](期待 [{string.Join(",", cs)}])");
        }
        if (e.PassedKeys is { } pk && !r.PassedKeys.SequenceEqual(pk))
            fails.Add($"素通ししたキー [{string.Join(",", r.PassedKeys)}](期待 [{string.Join(",", pk)}])");
        if (e.SentKeys is { } sk && !r.SentKeys.SequenceEqual(sk))
            fails.Add($"合成して送ったキー [{string.Join(",", r.SentKeys)}](期待 [{string.Join(",", sk)}])");
        if (e.Warnings is { } ws && !r.Warnings.SequenceEqual(ws))
            fails.Add($"警告 [{string.Join(",", r.Warnings)}](期待 [{string.Join(",", ws)}])");
        if (e.SavedSettings is { } want)
        {
            if (r.SavedSettings is null) fails.Add("設定画面で保存されていない");
            else
                foreach (var (k, v) in want)
                {
                    var got = SettingsSchema.Read(r.SavedSettings, k);
                    if (!string.Equals(got, v, StringComparison.OrdinalIgnoreCase)) fails.Add($"保存した設定 {k}={got}(期待 {v})");
                }
        }
        if (e.SettingsErrors is { } se && !r.SettingsErrors.SequenceEqual(se))
            fails.Add($"設定画面の保存の失敗 [{string.Join(" / ", r.SettingsErrors)}](期待 [{string.Join(" / ", se)}])");
        if (e.Runtime is { } rt &&!string.Equals(rt, r.Runtime, StringComparison.OrdinalIgnoreCase))
            fails.Add($"バックエンド {r.Runtime ?? "(読めていない)"}(期待 {rt})");
        if (e.Version is { } ver)
        {
            if (ver != r.Version) fails.Add($"版 {r.Version ?? "(無い)"}(期待 {ver})");
            if (r.TrayTooltip is null || !r.TrayTooltip.Contains(ver)) fails.Add($"トレイのツールチップ「{r.TrayTooltip}」に版 {ver} が出ていない");
        }
        if (e.Model is { } m && !string.Equals(m, r.Model, StringComparison.OrdinalIgnoreCase))
            fails.Add($"モデル {r.Model ?? "(読めていない)"}(期待 {m})");
        if (e.MaxReleaseToDeliverMs is { } max)
            foreach (var d in r.Deliveries.Where(d => d.ReleaseToDeliverMs > max))
                fails.Add($"{d.Seq} 番の発話: 離してから届くまで {d.ReleaseToDeliverMs} ms(上限 {max})");
        if (e.Screenshots is { } shots)
            foreach (var s in shots)
            {
                var rec = r.States.FirstOrDefault(x => x.State == s && x.Screenshot is not null);
                if (rec is null || (screenshotExists is not null && !screenshotExists(rec.Screenshot!)))
                    fails.Add($"状態「{s}」のスクリーンショットが無い");
            }
        if (e.Shots is { } xs)
            foreach (var x in xs)
            {
                var got = r.Shots.FirstOrDefault(s => s.Name == x.Name);
                if (got is null || got.Screenshot is null || (screenshotExists is not null && !screenshotExists(got.Screenshot)))
                {
                    fails.Add($"shot「{x.Name}」のスクリーンショットが無い");
                    continue;
                }
                if (x.State is not null && x.State != got.State) fails.Add($"shot「{x.Name}」の状態 {got.State}(期待 {x.State})");
                if (x.MinMeter is { } lo && got.Meter < lo) fails.Add($"shot「{x.Name}」の音量バー {got.Meter:0.00}(下限 {lo:0.00})");
                if (x.MaxMeter is { } hi && got.Meter > hi) fails.Add($"shot「{x.Name}」の音量バー {got.Meter:0.00}(上限 {hi:0.00})");
                if (x.MinInterimChars is { } ilo && got.InterimChars < ilo) fails.Add($"shot「{x.Name}」の途中経過 {got.InterimChars} 文字(下限 {ilo})");
                if (x.MaxInterimChars is { } ihi && got.InterimChars > ihi) fails.Add($"shot「{x.Name}」の途中経過 {got.InterimChars} 文字(上限 {ihi})");
                if (x.MinInterimLines is { } llo && got.InterimLines < llo) fails.Add($"shot「{x.Name}」の途中経過 {got.InterimLines} 行(下限 {llo})");
                if (x.MaxInterimLines is { } lhi && got.InterimLines > lhi) fails.Add($"shot「{x.Name}」の途中経過 {got.InterimLines} 行(上限 {lhi})");
                if (x.SameHintRowAs is { } other)
                {
                    var o = r.Shots.FirstOrDefault(s => s.Name == other);
                    if (o is null) fails.Add($"shot「{x.Name}」と比べる shot「{other}」が無い");
                    else if (o.HintRowX != got.HintRowX || o.HintRowY != got.HintRowY)
                        fails.Add($"shot「{x.Name}」の案内の行の位置 ({got.HintRowX},{got.HintRowY}) が shot「{other}」({o.HintRowX},{o.HintRowY}) と違う");
                }
            }
        return fails;
    }

    /// <summary>期待の notContains のうち、届いた文字列に残っているもの(NFKC で比べる)。</summary>
    private static IEnumerable<string> Absent(TextExpectation x, string got)
    {
        if (x.NotContains is not { Count: > 0 } ws) return [];
        var g = got.Normalize(NormalizationForm.FormKC);
        return ws.Where(w => w.Length > 0 && g.Contains(w.Normalize(NormalizationForm.FormKC), StringComparison.Ordinal));
    }

    /// <summary>期待の contains のうち、届いた文字列に無いもの(NFKC・大文字小文字を問わずに比べる)。</summary>
    private static IEnumerable<string> Missing(TextExpectation x, string got)
    {
        if (x.Contains is not { Count: > 0 } ws) return [];
        var g = got.Normalize(NormalizationForm.FormKC);
        return ws.Where(w => w.Length > 0 && !g.Contains(w.Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase));
    }
}
