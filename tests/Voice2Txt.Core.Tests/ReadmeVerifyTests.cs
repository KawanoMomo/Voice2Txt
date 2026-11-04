using System.Text.RegularExpressions;

namespace Voice2Txt.Core.Tests;

/// <summary>README の検証モードの説明が、台本の期待として受け付ける値と一致している。</summary>
public class ReadmeVerifyTests
{
    private static string Readme()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Voice2Txt.sln"))) return File.ReadAllText(Path.Combine(d.FullName, "README.md"));
        throw new InvalidOperationException("Voice2Txt.sln が見つからない");
    }

    [Fact]
    public void 取り消しの理由名の一覧はCancelReasonの値と一致する()
    {
        var text = Readme();
        int at = text.IndexOf("取り消しの理由名(`cancellations`", StringComparison.Ordinal);
        Assert.True(at >= 0, "README に取り消しの理由名の一覧が無い");
        int end = text.IndexOf("\n\n", at, StringComparison.Ordinal);
        var para = end < 0 ? text[at..] : text[at..end];
        // 「`名前` = 説明」の形で挙げた名前
        var listed = Regex.Matches(para, @"`([A-Za-z]+)` = ").Select(m => m.Groups[1].Value).ToHashSet();
        var actual = Enum.GetNames<CancelReason>().Where(n => n != nameof(CancelReason.None)).ToHashSet();
        Assert.Equal(actual.Order(), listed.Order());
    }

    /// <summary>README の「オーバーレイの状態表示」の表の行(状態の名前 → 本文の欄)。</summary>
    private static Dictionary<string, string> OverlayRows()
    {
        var text = Readme().Replace("\r\n", "\n");
        int at = text.IndexOf("- オーバーレイの状態表示:", StringComparison.Ordinal);
        Assert.True(at >= 0, "README に「オーバーレイの状態表示」が無い");
        int end = text.IndexOf("\n- ", at + 1, StringComparison.Ordinal);
        var para = end < 0 ? text[at..] : text[at..end];
        var rows = new Dictionary<string, string>();
        foreach (var line in para.Split('\n'))
        {
            var cells = line.Trim().Split('|');
            if (cells.Length < 6 || cells[1].Trim() is "状態" or "---") continue;
            rows[cells[1].Trim()] = cells[3].Trim();
        }
        return rows;
    }

    [Fact]
    public void オーバーレイの状態表示の表は非表示以外の全状態をOverlayViewの本文どおりに載せる()
    {
        var rows = OverlayRows();
        foreach (var s in Enum.GetValues<OverlayState>().Where(s => s != OverlayState.Hidden))
        {
            var label = OverlayView.LabelOf(s);
            Assert.True(rows.ContainsKey(label), $"README の状態表示の表に「{label}」が無い");
            // 例として載せる値: トークキーは初期値の右Ctrl、処理待ちは 1 件、詳細(取り消しの理由・準備の進み具合)は無し
            var expected = new OverlayView(s, PendingCount: s == OverlayState.ProcessingAndRecording ? 1 : 0).Text("右Ctrl");
            Assert.Equal($"`{expected}`", rows[label]);
        }
        Assert.True(rows.ContainsKey(OverlayView.UnavailableLabel), "README の状態表示の表に「使えない」が無い");
    }

    [Fact]
    public void 期待のcancellationsは理由名の配列と書いてある()
    {
        var text = Readme();
        Assert.Contains("`cancellations`(下の理由名の文字列の配列", text);
        Assert.Contains("[\"ModelNotReady\", \"TooShort\"]", text);
    }
}
