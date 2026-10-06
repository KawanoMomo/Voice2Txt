using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Voice2Txt.Core.Verification;

/// <summary>検証モードが <c>--out</c> に書く result.json。</summary>
public sealed class VerifyResult
{
    public string Scenario { get; set; } = "";
    public bool Completed { get; set; }
    public string? Error { get; set; }
    public string? Runtime { get; set; }
    public long? ModelReadyMs { get; set; }
    public List<DeliveryRecord> Deliveries { get; set; } = [];
    public List<CancelRecord> Cancellations { get; set; } = [];
    public List<StateRecord> States { get; set; } = [];
    public string Textbox { get; set; } = "";
    public string? Clipboard { get; set; }

    /// <summary>トークキーの判定で操作中のアプリへ素通ししたキー("RControlKey down" の形。順に)。</summary>
    public List<string> PassedKeys { get; set; } = [];

    /// <summary>押下中の別キーで、トークキーを修飾キーとして合成して送ったキー列("RControlKey down", "C down")。</summary>
    public List<string> SentKeys { get; set; } = [];

    /// <summary>設定の問題など、利用者に知らせた警告(例: talkKey を読めない)。</summary>
    public List<string> Warnings { get; set; } = [];

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
    public string? Screenshot { get; set; }
    public string? Capture { get; set; } // screen / render

    /// <summary>この状態を出した直後の本物の前面ウィンドウ: overlay / textbox / other。</summary>
    public string? Foreground { get; set; }
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
            }
            for (int i = 1; i < r.Deliveries.Count; i++)
                if (r.Deliveries[i].Seq < r.Deliveries[i - 1].Seq) fails.Add("届いた順が録音した順と違う");
        }
        if (e.Textbox is { Text: not null } tb)
        {
            var ratio = TextSimilarity.Ratio(tb.Text, r.Textbox);
            if (ratio < tb.Threshold) fails.Add($"テキスト欄の一致率 {ratio:0.00}(下限 {tb.Threshold:0.00})");
        }
        if (e.States is { } st)
        {
            var seen = r.States.Select(s => s.State).ToList();
            int k = 0;
            foreach (var s in seen) if (k < st.Count && s == st[k]) k++;
            if (k < st.Count) fails.Add($"状態「{st[k]}」が期待の順に現れない(実際: {string.Join(" → ", seen)})");
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
        if (e.Runtime is { } rt && !string.Equals(rt, r.Runtime, StringComparison.OrdinalIgnoreCase))
            fails.Add($"バックエンド {r.Runtime ?? "(読めていない)"}(期待 {rt})");
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
        return fails;
    }
}
