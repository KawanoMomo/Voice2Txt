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

    [Fact]
    public void 期待のcancellationsは理由名の配列と書いてある()
    {
        var text = Readme();
        Assert.Contains("`cancellations`(下の理由名の文字列の配列", text);
        Assert.Contains("[\"ModelNotReady\", \"TooShort\"]", text);
    }
}
