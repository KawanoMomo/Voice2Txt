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
        var all = Seg((0.4, "今日"), (1.0, "の会議では、"), (2.5, "The"), (3.3, " report."), (5.0, "という"), (5.8, "緊急の"), (6.5, "連絡。"));
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
        // 偽物の復号は、渡された音声の先頭 20 秒に終わる声の区間ごとに 1 区切り(文字は「語」1 つ、語の時刻は区間の中に 0.4 秒おき)を返して止まる
        var d = new FakeDecoder
        {
            Main = slice => Recovery.VoicedSpans(slice, 0.01).Where(sp => sp.End <= 20 * Rate)
                .Select(sp => new DecodedSegment("語", Enumerable.Range(0, 5).Select(k => new TimedToken((double)sp.Start / Rate + 0.2 + 0.4 * k, "語")).ToList(), (double)sp.End / Rate)).ToList(),
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
    public async Task 返し終えた後ろの短い結びの語も続きを復号して届ける()
    {
        // 約 33 秒の発話(詰めた後 30.55 秒)。復号は 28.82 秒で返し終え、後ろに 0.45 秒の「以上です」が残る(primary g01)
        var s = Voice(30.55, (0.15, 28.89), (29.70, 30.15));
        var d = new FakeDecoder
        {
            Main = slice => slice.Length == s.Length
                ? [new DecodedSegment("共有してもらいます。", [new TimedToken(28.0, "共有してもらいます。")], 28.82)]
                : [new DecodedSegment("以上です。", [new TimedToken(1.1, "以上です。")], 1.5)],
        };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("共有してもらいます。以上です。", r.Text);
        Assert.Equal(2, d.MainCalls.Count);
    }

    [Fact]
    public async Task 返し終えた所の後に短い息継ぎで続く文も続きを復号する()
    {
        var s = Voice(12, (0, 5.0), (5.3, 9.0)); // 文の切れ目の無音は 0.3 秒
        var d = new FakeDecoder
        {
            Main = slice => slice.Length == s.Length
                ? [new DecodedSegment("前の文。", [new TimedToken(2.0, "前の文。")], 5.0)]
                : [new DecodedSegment("後ろの文。", [new TimedToken(1.5, "後ろの文。")], 4.0)],
        };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("前の文。後ろの文。", r.Text);
        Assert.Equal(2, d.MainCalls.Count);
    }

    [Fact]
    public async Task 鳴り続ける音の途中で返し終えたら続けない()
    {
        var s = Voice(12, (0, 12)); // 話し終えた後も音楽が切れ目なく続く
        var d = new FakeDecoder { Main = _ => [new DecodedSegment("資料を送ります。", [new TimedToken(2.0, "資料を送ります。")], 5.0)] };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Equal("資料を送ります。", r.Text);
        Assert.Single(d.MainCalls);
    }

    [Fact]
    public async Task 途中経過で拾い直した区間は_確定版で同じ音なら復号し直さない()
    {
        var cache = new SpanCache();
        var d = new FakeDecoder { Main = _ => [Skipped], Span = (" Please confirm the hotel booking.", 0.01) };
        // 途中経過: 押下から 6 秒の写し(英文の区間は終わっている)
        var interim = await Recovery.TranscribeAsync(Mixed[..(6 * Rate)], 0.01, d, default, cache);
        Assert.Equal(1, interim.Recovered);
        Assert.Single(d.SpanCalls);
        // 確定版: 同じ英文の区間は前の結果を使う(2 回目の復号をしない)
        var final = await Recovery.TranscribeAsync(Mixed, 0.01, d, default, cache);
        Assert.Equal("来週の出張について、Please confirm the hotel booking.と英語で書かれたメール。", final.Text);
        Assert.Equal(1, final.Recovered);
        Assert.Equal(1, final.Reused);
        Assert.Single(d.SpanCalls);
    }

    [Fact]
    public async Task 区間の音が違えば前の結果を使わない()
    {
        var cache = new SpanCache();
        var d = new FakeDecoder { Main = _ => [Skipped], Span = (" Please confirm the hotel booking.", 0.01) };
        await Recovery.TranscribeAsync(Mixed, 0.01, d, default, cache);
        var other = (float[])Mixed.Clone();
        other[3 * Rate] = 0.29f; // 英文の区間の中の 1 サンプルだけ違う
        var r = await Recovery.TranscribeAsync(other, 0.01, d, default, cache);
        Assert.Equal(2, d.SpanCalls.Count);
        Assert.Equal(0, r.Reused);
    }

    [Fact]
    public void 覚えておく区間の数には上限があり_古いものから忘れる()
    {
        var cache = new SpanCache(capacity: 2);
        float[] a = [1, 2], b = [3, 4], c = [5, 6];
        cache.Add(a, ("A", 0)); cache.Add(b, ("B", 0)); cache.Add(c, ("C", 0));
        Assert.False(cache.TryGet([1, 2], out _));
        Assert.True(cache.TryGet([3, 4], out var got));
        Assert.Equal("B", got.Text);
        Assert.True(cache.TryGet([5, 6], out _));
    }

    // 途中経過の復号は英文の区間を英字で読めた(確定版では飛ばすことがある)
    private static readonly DecodedSegment Latin = Seg((0.4, "来週"), (1.0, "出張"), (1.4, "について、"), (2.5, " Please"), (3.0, " confirm"), (3.8, " booking."), (5.3, "と英語で"));

    [Fact]
    public async Task 途中経過で英字に読めた区間は先に拾い直しておき_確定版が飛ばしたら使う()
    {
        var cache = new SpanCache();
        var d = new FakeDecoder { Main = _ => [Latin], Span = (" Please confirm the hotel booking.", 0.01) };
        var interim = await Recovery.TranscribeAsync(Mixed[..(6 * Rate)], 0.01, d, default, cache, prefetch: true);
        Assert.Equal(Latin.Text, interim.Text); // 途中経過の文字列はそのまま
        Assert.Equal(0, interim.Recovered);
        Assert.Single(d.SpanCalls);
        d.Main = _ => [Skipped];
        var final = await Recovery.TranscribeAsync(Mixed, 0.01, d, default, cache);
        Assert.Equal("来週の出張について、Please confirm the hotel booking.と英語で書かれたメール。", final.Text);
        Assert.Equal(1, final.Reused);
        Assert.Single(d.SpanCalls);
    }

    [Fact]
    public async Task 先に拾い直すのは英字に読めて_終わった区間だけ()
    {
        var d = new FakeDecoder { Main = _ => [Latin], Span = ("x", 0) };
        // 英文の区間がまだ終わっていない(写しの終わりから 0.5 秒経っていない)
        await Recovery.TranscribeAsync(Mixed[..(int)(4.2 * Rate)], 0.01, d, default, new SpanCache(), prefetch: true);
        Assert.Empty(d.SpanCalls);
        // 日本語に読めた区間
        d.Main = _ => [Seg((0.4, "来週"), (1.0, "の出張で"), (2.5, "会議"), (3.5, "します"), (5.3, "と英語で"))];
        await Recovery.TranscribeAsync(Mixed, 0.01, d, default, new SpanCache(), prefetch: true);
        Assert.Empty(d.SpanCalls);
        // 確定版(prefetch なし)では先に拾い直さない
        d.Main = _ => [Latin];
        await Recovery.TranscribeAsync(Mixed, 0.01, d, default, new SpanCache());
        Assert.Empty(d.SpanCalls);
    }

    [Fact]
    public async Task 止められたら拾い直しの復号を始めない()
    {
        using var cts = new CancellationTokenSource();
        var d = new FakeDecoder { Main = _ => { cts.Cancel(); return [Skipped]; }, Span = (" Please confirm.", 0) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Recovery.TranscribeAsync(Mixed, 0.01, d, cts.Token));
        Assert.Empty(d.SpanCalls);
    }

    // 記号を読み上げる発話: 声は 0.2〜3.9 秒と 4.4〜6.1 秒(間の無音は短く、区間はどちらも語を持つ)。
    // 確定版の復号は「レポート、アンダーバー、最終、ドット、ピーディーエフ」を「report.pdf」にまとめ、port(1.66 秒)と p(5.36 秒)の間の語を飛ばした
    private static readonly float[] Symbol = Voice(6.6, (0.2, 3.9), (4.4, 6.1));
    private static readonly DecodedSegment Collapsed = Seg((-1, "[_BEG_]"), (0.26, "ファイル"), (0.6, "名"), (0.94, "は"), (1.2, "、"), (1.4, "re"), (1.66, "port"),
        (2.46, "."), (5.36, "p"), (5.86, "df"), (6.16, " です"), (6.44, "。"));

    [Fact]
    public async Task 声の区間の中で語の時刻が長く空いた所を拾い直して_後ろの語の前に差し込む()
    {
        var d = new FakeDecoder { Main = _ => [Collapsed], Span = ("underbar最終.p", 0) };
        var r = await Recovery.TranscribeAsync(Symbol, 0.01, d, default);
        Assert.Equal("ファイル名は、report.underbar最終.pdf です。", r.Text); // 切り出しに付いてきた後ろの語の「p」は重ねない
        Assert.Equal(1, r.Recovered);
        Assert.Equal(1, r.Holes);
        var cut = Assert.Single(d.SpanCalls);
        Assert.InRange(cut.Length / (double)Rate, 5.36 - 1.66 - 0.01, 5.36 - 1.66 + 0.01); // 前後の語の時刻の間だけを切り出す
    }

    [Theory]
    [InlineData(" Two.")] // 雑音を読んだ(日本語の文字が無い)
    [InlineData("Ому")]
    [InlineData("report")] // 確定版に既にある
    [InlineData("p")] // 重なりを落とすと何も残らない
    public async Task 語の穴から拾い直した結果が日本語の文字を持たないか重複なら差し込まない(string got)
    {
        var d = new FakeDecoder { Main = _ => [Collapsed], Span = (got, 0) };
        var r = await Recovery.TranscribeAsync(Symbol, 0.01, d, default);
        Assert.Equal(Collapsed.Text, r.Text);
        Assert.Equal(0, r.Recovered);
    }

    [Fact]
    public async Task 語の間の声が短ければ穴とみなさない()
    {
        // ゆっくり読んだ数字: 「25」(5.8 秒)の後ろの「日」まで約 1 秒の声
        var s = Voice(8, (0.2, 7.8));
        var d = new FakeDecoder { Main = _ => [Seg((0.5, "締め日は"), (1.4, "毎月"), (5.8, "25"), (6.84, "日"), (7.5, "です。"))], Span = ("15日", 0) };
        var r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Single(d.SpanCalls); // 「毎月」(1.4)〜「25」(5.8)の 4.4 秒だけが穴
        d.SpanCalls.Clear();
        d.Main = _ => [Seg((0.5, "締め日は"), (1.5, "毎月"), (2.5, "の"), (3.5, "末"), (4.6, "日"), (5.8, "25"), (6.84, "日"), (7.5, "です。"))];
        r = await Recovery.TranscribeAsync(s, 0.01, d, default);
        Assert.Empty(d.SpanCalls);
        Assert.Equal(0, r.Recovered);
    }

    [Fact]
    public async Task 語の無い声の区間を含む穴は区間の拾い直しに任せる()
    {
        // 英文の区間(2.2〜4.0 秒)は語が無く、前後の語「について」(1.4)と「と」(5.0)の間は 1.8 秒の声: 同じ英文を 2 回差し込まない
        var d = new FakeDecoder { Main = _ => [Skipped], Span = (" Please confirm the hotel booking.", 0.01) };
        var r = await Recovery.TranscribeAsync(Mixed, 0.01, d, default);
        Assert.Equal("来週の出張について、Please confirm the hotel booking.と英語で書かれたメール。", r.Text);
        Assert.Single(d.SpanCalls);
        Assert.Equal(0, r.Holes);
    }

    [Fact]
    public async Task 途中経過で拾い直した語の穴は_確定版で同じ音なら復号し直さない()
    {
        var cache = new SpanCache();
        var d = new FakeDecoder { Main = _ => [Collapsed], Span = ("underbar最終.p", 0) };
        // 写しの終わりが後ろの語「p」(5.36 秒)から 0.5 秒経っていない: まだ拾い直さない
        await Recovery.TranscribeAsync(Symbol[..(int)(5.6 * Rate)], 0.01, d, default, cache, prefetch: true);
        Assert.Empty(d.SpanCalls);
        var interim = await Recovery.TranscribeAsync(Symbol[..(int)(6.0 * Rate)], 0.01, d, default, cache, prefetch: true);
        Assert.Equal(1, interim.Holes);
        Assert.Single(d.SpanCalls);
        var final = await Recovery.TranscribeAsync(Symbol, 0.01, d, default, cache);
        Assert.Equal("ファイル名は、report.underbar最終.pdf です。", final.Text);
        Assert.Equal(1, final.Reused);
        Assert.Single(d.SpanCalls); // 離してから届くまでに 2 回目の復号を足さない
    }

    [Theory]
    [InlineData("report.pdf", 7, "underbar最終.p", "underbar最終.")]
    [InlineData("reportpdf", 6, "port_最終", "_最終")]
    [InlineData("report.pdf", 7, "最終", "最終")]
    [InlineData("report.pdf", 7, "PDF", "")]
    public void 差し込む文字列の前後の語と重なる所を落とす(string text, int at, string got, string expected) =>
        Assert.Equal(expected, Recovery.TrimOverlap(text, at, got));

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
