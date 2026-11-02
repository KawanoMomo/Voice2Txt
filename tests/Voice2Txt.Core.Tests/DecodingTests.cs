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
    public void 初期プロンプトは数える列の終わりの十を数字で見せる()
    {
        // 「…ここのつ、とお。」の「とお」は「つ」が付かない短い数で、前置きが無いと「等」になる
        Assert.EndsWith("9つ、10。", Decoding.InitialPrompt);
    }

    [Fact]
    public void 初期プロンプトは短くかなと数字と句読点だけ()
    {
        // 長い・内容のあるプロンプトは無音や短い発話でそのまま出てくる(幻聴)種になる。漢字の語は入れない
        Assert.True(Decoding.InitialPrompt.Length <= 32);
        Assert.All(Decoding.InitialPrompt, c => Assert.True(c is >= 'ぁ' and <= 'ゖ' or 'ー' or '、' or '。' or >= '0' and <= '9', $"かな・数字以外: {c}"));
    }

    [Fact]
    public void 速さを優先する設定は既定でオフ_写しても残る()
    {
        Assert.False(new AppSettings().PreferSpeed);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettings.Json);
        Assert.Contains("\"preferSpeed\": false", json);
        var t = new AppSettings();
        t.CopyFrom(new AppSettings { PreferSpeed = true });
        Assert.True(t.PreferSpeed);
    }

    [Theory]
    [InlineData(0.5, 500)]   // 短い発話も 10 秒の窓より縮めない
    [InlineData(5.0, 500)]   // 5 × 1.5 + 2 = 9.5 秒
    [InlineData(5.5, 600)]   // 10.25 秒 → 2 秒刻みに切り上げて 12 秒
    [InlineData(11.0, 1000)] // 18.5 秒 → 20 秒
    [InlineData(17.0, 1400)] // 27.5 秒 → 28 秒
    [InlineData(18.7, 0)]    // 30 秒の窓に届く = 縮めない
    [InlineData(45.0, 0)]    // 30 秒の窓に収まらない音声は縮めない(窓の外を読み落とさない)
    public void 速さを優先するときの窓は音声の長さの1_5倍に余白を足して刻みに切り上げる(double seconds, int expected) =>
        Assert.Equal(expected, Decoding.AudioContext((int)(seconds * Audio.SampleRate)));

    [Fact]
    public void 窓は音声の末尾より後ろまで届く()
    {
        for (double sec = 0.2; sec < 18; sec += 0.37)
        {
            int ctx = Decoding.AudioContext((int)(sec * Audio.SampleRate));
            if (ctx == 0) ctx = Decoding.FullAudioContext; // 縮めない = 30 秒の窓
            Assert.True(ctx * 30.0 / Decoding.FullAudioContext >= sec * Decoding.AudioContextRatio + Decoding.AudioContextMarginSeconds - 1e-9, $"{sec}s → {ctx}");
            Assert.Equal(0, ctx % Decoding.AudioContextStep);
        }
    }

    [Fact]
    public void 縮めた窓の復号で同じ文の繰り返しと音声より後ろの区切りは崩れとみなす()
    {
        const string s = "画面の配置について意見をください。";
        // 実測(窓 4 秒): 2.74 秒の音声で「0.00-2.26 文」「2.26-2.26 同じ文」「2.26-5.16 同じ文」
        Assert.False(Decoding.Derailed(2.74, null, 0.00, 2.26, s));
        Assert.True(Decoding.Derailed(2.74, s, 2.26, 2.26, s));
        Assert.True(Decoding.Derailed(2.74, s, 2.26, 5.16, " " + s));
        // 実測(窓 12 秒): 9.17 秒の音声で最後の区切りが 24.58 秒まで
        Assert.True(Decoding.Derailed(9.17, "a", 8.64, 24.58, "b"));
        // 実測(窓 20 秒): 文の後ろの句だけを繰り返した区切り。続きの復号(前の復号の文字列を渡す)の「以上です。」の繰り返しも同じ
        Assert.True(Decoding.Derailed(11.0, "設計書に案を追記しましたので、確認をお願いします。", 8.0, 10.0, "確認をお願いします。"));
        Assert.True(Decoding.Derailed(1.7, "次の会議まで結果を共有してもらいます。以上です。", 0.0, 1.5, "以上です。"));
        Assert.False(Decoding.Derailed(1.7, "ここまでの文。", 0.0, 1.5, "。")); // 文字の無い区切りは比べない
        // 普通の続きの区切り・音声の少し後ろまでの区切りは崩れでない
        Assert.False(Decoding.Derailed(9.17, "来週の出張について、", 2.40, 8.64, "と英語で書かれたメールが届いたので、"));
        Assert.False(Decoding.Derailed(9.17, null, 0, 10.5, "最初の区切り"));
        // 実測(窓 10 秒): 小さい声の 4.13 秒の音声で、最初の(唯一の)区切りの終わりが 17.76 秒。捨てると発話が消える
        Assert.False(Decoding.Derailed(4.13, "", 0, 17.76, "新しい資料は、共有フォルダに保存してあります。"));
    }

    [Theory]
    [InlineData("以上です。以上です。", "以上です。")]
    [InlineData("画面の配置について意見をください。画面の配置について意見をください。画面の配置について意見をください。", "画面の配置について意見をください。")]
    [InlineData("確認をお願いします。確認をお願いします。", "確認をお願いします。")]
    [InlineData("案を追記しましたので、確認をお願いします。確認をお願いします。", "案を追記しましたので、確認をお願いします。")]
    [InlineData("申請書は、総務に出してください。", "申請書は、総務に出してください。")]
    [InlineData("ここです。ここです", "ここです。ここです")]          // 末尾が文の終わりでない
    [InlineData("出してください。てください。", "出してください。てください。")] // 単位が文の頭から始まらない
    [InlineData("。。", "。。")]
    public void 区切りの末尾で同じ文が続けば1回分にする(string text, string expected) =>
        Assert.Equal(expected, Decoding.TrimRepeatedTail(text));
}
