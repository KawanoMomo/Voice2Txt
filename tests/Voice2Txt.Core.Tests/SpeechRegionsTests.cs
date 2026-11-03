using Voice2Txt.Core;

namespace Voice2Txt.Core.Tests;

/// <summary>30 秒を超える発話を声の区間に分けて Haas に渡す(BLK-human-20261102-1015-3)。</summary>
public class SpeechRegionsTests
{
    private const int Sr = Audio.SampleRate;

    /// <summary>(秒, 振幅) の並び。振幅 0 は無音(小さな雑音)、それ以外は 220 Hz の正弦波(声の代わり)。</summary>
    private static float[] Make(params (double Sec, double Amp)[] parts)
    {
        var list = new List<float>();
        var rnd = new Random(1);
        foreach (var (sec, amp) in parts)
            for (int i = 0; i < (int)(sec * Sr); i++)
                list.Add(amp == 0 ? (float)((rnd.NextDouble() - 0.5) * 0.0004) : (float)(amp * Math.Sin(2 * Math.PI * 220 * list.Count / Sr)));
        return [.. list];
    }

    private static double Sec(int samples) => samples / (double)Sr;

    [Fact]
    public void Sentences_SeparatedBySilence_BecomeRegions_WithPadding()
    {
        // 6 文 × 5 秒、間に 0.8 秒の無音(TrimSilence が詰めた後の間と同じくらい)
        var parts = new List<(double, double)> { (0.5, 0) };
        for (int i = 0; i < 6; i++) { parts.Add((5, 0.3)); parts.Add((0.8, 0)); }
        var a = Make([.. parts]);
        var r = SpeechRegions.Find(a, 0.01);
        Assert.Equal(6, r.Count);
        Assert.All(r, x => Assert.InRange(Sec(x.End - x.Start), 5.0, 5.0 + 2 * SpeechRegions.PadSeconds + 0.05));
        for (int i = 1; i < r.Count; i++) Assert.True(r[i].Start >= r[i - 1].End, "区間が重なる");
        Assert.InRange(Sec(r[0].Start), 0.15, 0.25); // 0.5 秒目に始まる声の 0.3 秒前
    }

    [Fact]
    public void LongSpeechWithoutPause_IsCutAtQuietestPlace_AtMostTenSeconds()
    {
        // 25 秒切れ目なく話し、12 秒目に 0.2 秒だけ小さくなる所がある
        var a = Make((12, 0.3), (0.2, 0.02), (12.8, 0.3));
        var r = SpeechRegions.Find(a, 0.01);
        Assert.True(r.Count >= 3);
        Assert.All(r, x => Assert.True(Sec(x.End - x.Start) <= SpeechRegions.MaxRegionSeconds + 0.01, $"{Sec(x.End - x.Start):F2} 秒"));
        Assert.Contains(r, x => Math.Abs(Sec(x.End) - 12.1) < 0.15); // 小さくなった所で切る
        // 声の部分は全部どこかの区間に入る(抜けない)
        Assert.Equal(0, r[0].Start);
        Assert.Equal(a.Length, r[^1].End);
        for (int i = 1; i < r.Count; i++) Assert.Equal(r[i - 1].End, r[i].Start);
    }

    [Fact]
    public void QuietSentence_AmongLoudOnes_IsNotDropped()
    {
        // 大きい文の間に、30 dB 小さい文(配布元の VAD が丸ごと落としやすい音量)を挟む
        var a = Make((0.5, 0), (8, 0.3), (1, 0), (4, 0.0095), (1, 0), (8, 0.3), (1, 0), (8, 0.3), (0.5, 0));
        var r = SpeechRegions.Find(a, 0.01);
        int quietMid = (int)((0.5 + 8 + 1 + 2) * Sr);
        Assert.Contains(r, x => x.Start <= quietMid && quietMid < x.End);
    }

    [Fact]
    public void ShortClick_IsNotARegion_AndSilenceOnlyGivesWhole()
    {
        var a = Make((2, 0), (0.1, 0.3), (2, 0), (5, 0.3), (2, 0));
        var r = SpeechRegions.Find(a, 0.01);
        Assert.Single(r);
        Assert.InRange(Sec(r[0].Start), 3.75, 3.85); // 4.1 秒目に始まる声の 0.3 秒前(0.1 秒の物音は区間にしない)
        var silent = Make((3, 0));
        Assert.Equal([(0, silent.Length)], SpeechRegions.Find(silent, 0.01));
    }

    [Theory]
    [InlineData(new[] { "今日は晴れです。", "明日は雨です。" }, "今日は晴れです。明日は雨です。")]
    [InlineData(new[] { "Please confirm", "the hotel booking." }, "Please confirm the hotel booking.")]
    [InlineData(new[] { "出張について、", " Please confirm.", "と書かれていた。" }, "出張について、Please confirm.と書かれていた。")]
    [InlineData(new[] { "", "  ", "一つ" }, "一つ")]
    public void Join_SpacesOnlyBetweenLatinWords(string[] parts, string expected) => Assert.Equal(expected, SpeechRegions.Join(parts));

    private sealed class Recorder : IEngine
    {
        public readonly List<(int Length, bool Interim)> Calls = [];
        public string Runtime => "Vulkan";
        public Task<string> TranscribeAsync(float[] s, IProgress<string>? p, CancellationToken ct) { Calls.Add((s.Length, false)); return Task.FromResult($"文{Calls.Count}。"); }
        public Task<string> TranscribeInterimAsync(float[] s, CancellationToken ct) { Calls.Add((s.Length, true)); return Task.FromResult($"途{Calls.Count}"); }
        public void Dispose() { }
    }

    [Fact]
    public async Task UpToThirtySeconds_PassesWholeOnce()
    {
        var inner = new Recorder();
        var t = new RegionTranscriber(inner, 0.01);
        var a = Make((1, 0), (10, 0.3), (2, 0), (10, 0.3), (1, 0)); // 24 秒
        Assert.Equal("文1。", await t.TranscribeAsync(a, null, CancellationToken.None));
        Assert.Equal([(a.Length, false)], inner.Calls);
        Assert.Equal("途2", await t.TranscribeInterimAsync(a, CancellationToken.None));
    }

    [Fact]
    public async Task OverThirtySeconds_EachRegionInOrder_Joined_LogsCountsOnly()
    {
        var inner = new Recorder();
        var logs = new List<string>();
        var t = new RegionTranscriber(inner, 0.01, logs.Add);
        var parts = new List<(double, double)> { (0.5, 0) };
        for (int i = 0; i < 7; i++) { parts.Add((5, 0.3)); parts.Add((0.8, 0)); } // 約 41 秒
        var a = Make([.. parts]);
        var text = await t.TranscribeAsync(a, null, CancellationToken.None);
        Assert.Equal(7, inner.Calls.Count);
        Assert.Equal("文1。文2。文3。文4。文5。文6。文7。", text);
        Assert.All(inner.Calls, c => Assert.False(c.Interim));
        Assert.Single(logs);
        Assert.StartsWith("regions n=7 sec=41.", logs[0]);
        // 途中経過も同じ区間で(押下中に 30 秒を超えた写し)
        inner.Calls.Clear();
        await t.TranscribeInterimAsync(a, CancellationToken.None);
        Assert.Equal(7, inner.Calls.Count);
        Assert.All(inner.Calls, c => Assert.True(c.Interim));
    }
}
