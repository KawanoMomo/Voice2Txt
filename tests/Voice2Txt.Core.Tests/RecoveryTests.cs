using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt.Core.Tests;

public class RecoveryTests
{
    private const int Rate = Audio.SampleRate;

    /// <summary>声の代わりの音(0.3 の正弦波)を [開始, 終了) 秒に置いた音声。それ以外は無音。</summary>
    private static float[] Voice(double total, params (double From, double To)[] parts)
    {
        var s = new float[(int)(total * Rate)];
        foreach (var (a, b) in parts)
            for (int i = (int)(a * Rate); i < Math.Min(s.Length, (int)(b * Rate)); i++) s[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 300 * i / Rate));
        return s;
    }

    private static DecodedSegment Seg(params (double At, string Text)[] tokens) =>
        new(string.Concat(tokens.Where(t => !t.Text.StartsWith("[_")).Select(t => t.Text)), tokens.Select(t => new TimedToken(t.At, t.Text)).ToList());

    /// <summary>確定版の復号は台本どおりの区切りを返し、拾い直しの復号は決めた文字列を返す偽物。</summary>
    private sealed class FakeDecoder : ISpanDecoder
    {
        public Func<float[], IReadOnlyList<DecodedSegment>> Main = _ => [];
        public (string Text, double NoSpeech) Span = ("", 0);
        public readonly List<float[]> MainCalls = new();
        public readonly List<float[]> SpanCalls = new();

        public Task<IReadOnlyList<DecodedSegment>> DecodeAsync(float[] samples, CancellationToken ct) { MainCalls.Add(samples); return Task.FromResult(Main(samples)); }
        public Task<(string Text, double NoSpeech)> DecodeSpanAsync(float[] samples, CancellationToken ct) { SpanCalls.Add(samples); return Task.FromResult(Span); }
    }

    // 日本語の声(0〜1.5 秒)、別の話者の英文(2.2〜4.0 秒)、日本語の声(4.8〜7.0 秒)。確定版の復号は英文を飛ばした
    private static readonly float[] Mixed = Voice(7.2, (0, 1.5), (2.2, 4.0), (4.8, 7.0));
    private static readonly DecodedSegment Skipped = Seg((-1, "[_BEG_]"), (0.4, "来週"), (0.7, "の"), (1.0, "出張"), (1.4, "について"), (4.1, "、"),
        (5.0, "と"), (5.3, "英語"), (5.6, "で"), (6.0, "書かれた"), (6.5, "メール"), (6.9, "。"), (-1, "[_TT_350]"));

    [Fact]
    public async Task 文字の無い声の区間を拾い直して_その位置に差し込む()
    {
        var d = new FakeDecoder { Main = _ => [Skipped], Span = (" Please confirm the hotel booking.", 0.01) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal("来週の出張について、Please confirm the hotel booking.と英語で書かれたメール。", r.Text);
        Assert.Equal(1, r.Recovered);
        var cut = Assert.Single(d.SpanCalls);
        Assert.InRange(cut.Length / (double)Rate, 1.8, 1.8 + 2 * Recovery.SpanPadSeconds + 0.1); // 英文の区間だけを切り出す
    }

    [Fact]
    public async Task 全部の声の区間に語があれば拾い直さない()
    {
        var all = Seg((0.4, "今日"), (1.0, "の会議では、"), (2.5, "The"), (3.3, " report."), (5.0, "という"), (6.5, "連絡。"));
        var d = new FakeDecoder { Main = _ => [all], Span = ("余計な文", 0) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal(all.Text, r.Text);
        Assert.Equal(0, r.Recovered);
        Assert.Empty(d.SpanCalls);
    }

    [Theory]
    [InlineData("(拍手)", 0.0)]
    [InlineData("[音楽]", 0.0)]
    [InlineData("♪", 0.0)]
    [InlineData("Please confirm the hotel booking.", 0.9)] // 声が無い見込みが高い
    [InlineData("と英語で", 0.0)] // 既に確定版にある
    [InlineData("、", 0.0)]
    public async Task 拾い直した結果が注記や声の無い文や重複なら差し込まない(string got, double noSpeech)
    {
        var d = new FakeDecoder { Main = _ => [Skipped], Span = (got, noSpeech) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal(Skipped.Text, r.Text);
        Assert.Equal(0, r.Recovered);
    }

    [Fact]
    public async Task 語の時刻を出せないエンジンでは拾い直さない()
    {
        var noTimes = Seg((-1, "来週の出張について、"), (-1, "と英語で書かれたメール。"));
        var d = new FakeDecoder { Main = _ => [noTimes], Span = ("Please confirm.", 0) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal("来週の出張について、と英語で書かれたメール。", r.Text);
        Assert.Empty(d.SpanCalls);
    }

    [Fact]
    public async Task 咳のような短い音は拾い直さない()
    {
        var s = Voice(5, (0, 1.5), (2.2, 2.5), (3.2, 4.8));
        var d = new FakeDecoder { Main = _ => [Seg((0.5, "資料を"), (1.4, "送ります。"), (3.6, "確認して"), (4.6, "ください。"))], Span = ("ご視聴ありがとうございました", 0) };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("資料を送ります。確認してください。", r.Text);
        Assert.Empty(d.SpanCalls);
    }

    [Fact]
    public async Task 最後の区間が欠けたら末尾に足す()
    {
        var s = Voice(5, (0, 1.5), (2.2, 4.5));
        var d = new FakeDecoder { Main = _ => [Seg((0.5, "最後に"), (1.4, "一言、"))], Span = (" Thank you.", 0) };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("最後に一言、Thank you.", r.Text);
    }

    [Fact]
    public async Task 英文の間に差し込むときは空白で区切る()
    {
        var d = new FakeDecoder { Main = _ => [Seg((0.5, "Hello"), (1.4, " all."), (5.5, " See"), (6.5, " you."))], Span = ("Please wait", 0) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal("Hello all. Please wait See you.", r.Text);
    }

    [Fact]
    public void 割れた多バイト文字があっても語の位置を合わせる()
    {
        var text = "届いたので、返信を";
        var tokens = new[] { "�", "�", "いた", "ので", "、", "�", "�", "を" }.Select(t => new TimedToken(0, t)).ToList();
        Assert.Equal(new[] { 0, 0, 1, 3, 5, 6, 6, 8 }, Recovery.Align(text, tokens));
    }

    [Fact]
    public async Task 復号が途中で返し終えたら_最後の区切りの終わりから続きを復号する()
    {
        // 72 秒: 2 秒話して 1 秒黙るを繰り返す(区間は 24 個)
        var parts = Enumerable.Range(0, 24).Select(i => (i * 3.0, i * 3.0 + 2)).ToArray();
        var s = Voice(72, parts);
        // 偽物の復号は、渡された音声の先頭 20 秒に終わる声の区間ごとに 1 区切り(語 1 つ、時刻は区間の中ほど)を返して止まる
        var d = new FakeDecoder
        {
            Main = slice => Recovery.VoicedSpans(slice, 0.01).Where(sp => sp.End <= 20 * Rate)
                .Select(sp => new DecodedSegment("語", [new TimedToken((sp.Start + sp.End) / 2.0 / Rate, "語")], (double)sp.End / Rate)).ToList(),
            Span = ("余計な文", 0),
        };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal(new string('語', 24), r.Text); // 後ろが消えない・重ならない
        Assert.True(d.MainCalls.Count >= 4);
        for (int i = 1; i < d.MainCalls.Count; i++) Assert.True(d.MainCalls[i].Length < d.MainCalls[i - 1].Length);
        Assert.Empty(d.SpanCalls); // 続きの語の時刻を続きの先頭からずらし直している
    }

    [Fact]
    public async Task 返し終えた所より後ろに声が無ければ続けない()
    {
        var s = Voice(5, (0, 3), (4.0, 4.2)); // 話し終えた後に短い物音
        var d = new FakeDecoder { Main = _ => [new DecodedSegment("資料を送ります。", [new TimedToken(1.0, "資料"), new TimedToken(2.8, "送ります。")], 3.0)] };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("資料を送ります。", r.Text);
        Assert.Single(d.MainCalls);
    }

    [Fact]
    public void 台本の_contains_に無い語を失敗にする()
    {
        var e = new Expectation { Deliveries = [new() { Text = "来週の出張について、と英語で", MinSimilarity = 0.1, Contains = ["hotel booking"] }] };
        var r = new VerifyResult { Completed = true, Deliveries = [new() { Seq = 1, To = "textbox", Text = "来週の出張について、と英語で" }] };
        Assert.Contains(Evaluator.Check(e, r), f => f.Contains("1 件目に「hotel booking」が無い"));
        r.Deliveries[0].Text = "来週の出張について、Please confirm the Hotel Booking.と英語で";
        Assert.Empty(Evaluator.Check(e, r));
    }
}
