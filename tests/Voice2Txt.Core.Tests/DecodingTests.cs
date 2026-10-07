using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

public class DecodingTests
{
    [Fact]
    public void 言語は日本語固定() => Assert.Equal("ja", Decoding.Language);

    [Theory]
    [InlineData("ええと")]
    [InlineData("えーと")]
    [InlineData("えっと")]
    [InlineData("あの")]
    public void 初期プロンプトは言い淀みをかなで見せる(string filler) => Assert.Contains(filler, Decoding.InitialPrompt);

    [Fact]
    public void 初期プロンプトは短くかなと句読点だけ()
    {
        // 長い・内容のあるプロンプトは無音や短い発話でそのまま出てくる(幻聴)種になる
        Assert.True(Decoding.InitialPrompt.Length <= 32);
        Assert.All(Decoding.InitialPrompt, c => Assert.True(c is >= 'ぁ' and <= 'ゖ' or 'ー' or '、' or '。', $"かな以外: {c}"));
    }
}
