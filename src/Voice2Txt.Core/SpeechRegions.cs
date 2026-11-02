namespace Voice2Txt.Core;

/// <summary>
/// 長い発話を声の区間(発話区間)に分ける。Haas(FastConformer)は 1 回の要求に文がいくつも入ると文を丸ごと落とすので、
/// <see cref="LongSeconds"/> 秒を超える録音は区間ごとに文字起こししてつなぐ。区切り方は speech.cpp の <c>speech asr --vad</c> の既定に合わせる
/// (区間の前後に 0.3 秒の余白、0.5 秒の無音で区間を終える、区間は 10 秒まで(超えれば中の最も静かな所で切る)、0.25 秒未満の音は区間にしない)。
/// 声の判定は無音の取り消しと同じ、その録音の大きさに合わせた下限(<see cref="Audio.VoiceLevel"/>)を使う(小さい声の区間を落とさない)。
/// </summary>
public static class SpeechRegions
{
    /// <summary>これより長い録音を区間に分ける(秒)。これ以下は今までどおり 1 回で渡す。</summary>
    public const double LongSeconds = 30;
    public const double MaxRegionSeconds = 10;
    public const double PadSeconds = 0.3;
    public const double MinSilenceSeconds = 0.5;
    public const double MinSpeechSeconds = 0.25;

    /// <summary>区間(サンプル位置の [開始, 終了)。順に並び、重ならない)。声が見つからなければ全体を 1 つ。</summary>
    public static List<(int Start, int End)> Find(float[] samples, double threshold, int sampleRate = Audio.SampleRate)
    {
        double level = Audio.VoiceLevel(samples, threshold, sampleRate);
        var cut = new List<(int Start, int End)>();
        foreach (var s in Recovery.VoicedSpans(samples, level, sampleRate, MinSilenceSeconds))
        {
            if (s.End - s.Start < MinSpeechSeconds * sampleRate) continue;
            Cut(samples, s.Start, s.End, (int)(MaxRegionSeconds * sampleRate) - 2 * (int)(PadSeconds * sampleRate), sampleRate, cut);
        }
        if (cut.Count == 0) return [(0, samples.Length)];
        // 余白を足す。隣の区間の余白と重なるなら、間の真ん中で分ける(同じ音を 2 回渡さない)
        int pad = (int)(PadSeconds * sampleRate);
        var res = new List<(int Start, int End)>(cut.Count);
        for (int i = 0; i < cut.Count; i++)
        {
            int a = Math.Max(0, cut[i].Start - pad), b = Math.Min(samples.Length, cut[i].End + pad);
            if (i > 0 && a < res[^1].End)
            {
                int mid = (cut[i - 1].End + cut[i].Start) / 2;
                res[^1] = (res[^1].Start, mid);
                a = mid;
            }
            res.Add((a, b));
        }
        return res;
    }

    /// <summary>[start, end) が max より長ければ、両端から 1 秒以上離れた所で 100 ms の窓の RMS が最も小さい所で切る(切れた先も同じ)。</summary>
    private static void Cut(float[] samples, int start, int end, int max, int sampleRate, List<(int, int)> outp)
    {
        if (end - start <= max) { outp.Add((start, end)); return; }
        int win = sampleRate / 10, step = sampleRate * 30 / 1000, margin = Math.Min(sampleRate, (end - start) / 4);
        int best = -1; double bestRms = double.MaxValue;
        for (int i = start + margin; i + win <= end - margin; i += step)
        {
            double r = Audio.Rms(samples.AsSpan(i, win));
            if (r < bestRms) { bestRms = r; best = i + win / 2; }
        }
        if (best < 0) best = start + (end - start) / 2;
        Cut(samples, start, best, max, sampleRate, outp);
        Cut(samples, best, end, max, sampleRate, outp);
    }

    /// <summary>区間ごとの文字列をつなぐ(日本語・中国語の側か、どちらかに空白があればそのまま。英数字どうしは空白を 1 つ挟む)。</summary>
    public static string Join(IEnumerable<string> texts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in texts)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0 && IsAsciiWord(sb[^1]) && IsAsciiWord(t[0])) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static bool IsAsciiWord(char c) => c < 128 && (char.IsLetterOrDigit(c) || c is '.' or ',' or '!' or '?');
}

/// <summary>
/// 長い発話(<see cref="SpeechRegions.LongSeconds"/> 秒超)を区間に分けて 1 つずつ内側のエンジンに渡し、順につなぐ(Haas エンジンに被せる)。
/// それ以下は内側にそのまま渡す。ログには区間の数と長さだけを書く(本文は書かない)。
/// </summary>
public sealed class RegionTranscriber(IEngine inner, double threshold, Action<string>? log = null) : IEngine
{
    public string Runtime => inner.Runtime;

    public Task<string> TranscribeAsync(float[] samples16k, IProgress<string>? partial, CancellationToken ct) => RunAsync(samples16k, false, ct);

    public Task<string> TranscribeInterimAsync(float[] samples16k, CancellationToken ct) => RunAsync(samples16k, true, ct);

    private async Task<string> RunAsync(float[] samples, bool interim, CancellationToken ct)
    {
        if (samples.Length <= SpeechRegions.LongSeconds * Audio.SampleRate)
            return interim ? await inner.TranscribeInterimAsync(samples, ct) : await inner.TranscribeAsync(samples, null, ct);
        var regions = SpeechRegions.Find(samples, threshold);
        var texts = new List<string>(regions.Count);
        foreach (var (a, b) in regions)
        {
            var part = samples[a..b];
            texts.Add(interim ? await inner.TranscribeInterimAsync(part, ct) : await inner.TranscribeAsync(part, null, ct));
        }
        if (!interim)
            log?.Invoke($"regions n={regions.Count} sec={samples.Length / (double)Audio.SampleRate:F1} max={regions.Max(r => r.End - r.Start) / (double)Audio.SampleRate:F1}");
        return SpeechRegions.Join(texts);
    }

    public void Dispose() => inner.Dispose();
}
