namespace Voice2Txt.Core;

public static class Audio
{
    public const int SampleRate = 16000;

    /// <summary>WAV(PCM 16/24/32 bit・IEEE float、任意のチャンネル数とサンプルレート)を 16 kHz モノラルの float に読む。</summary>
    public static float[] ReadWav16kMono(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("RIFF ではない");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("WAVE ではない");
        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
                if (format == unchecked((short)0xFFFE)) format = bits == 32 ? 3 : 1; // WAVE_FORMAT_EXTENSIBLE を簡略に扱う
            }
            else if (id == "data") { data = br.ReadBytes(size); }
            else br.ReadBytes(size);
            if ((size & 1) == 1 && br.BaseStream.Position < br.BaseStream.Length) br.ReadByte();
        }
        if (data is null || channels == 0) throw new InvalidDataException("data か fmt が無い");
        int bps = bits / 8, frames = data.Length / (bps * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bps;
                sum += (format, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(data, o),
                    (_, 16) => BitConverter.ToInt16(data, o) / 32768.0,
                    (_, 24) => ((data[o] | data[o + 1] << 8 | (sbyte)data[o + 2] << 16)) / 8388608.0,
                    (_, 32) => BitConverter.ToInt32(data, o) / 2147483648.0,
                    (_, 8) => (data[o] - 128) / 128.0,
                    _ => throw new InvalidDataException($"未対応の形式 {format}/{bits}bit"),
                };
            }
            mono[f] = (float)(sum / channels);
        }
        return Resample(mono, rate, SampleRate);
    }

    public static float[] Resample(float[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        int n = (int)((long)src.Length * to / from);
        var dst = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double p = i * step; int a = (int)p; double t = p - a;
            float s0 = src[Math.Min(a, src.Length - 1)], s1 = src[Math.Min(a + 1, src.Length - 1)];
            dst[i] = (float)(s0 + (s1 - s0) * t);
        }
        return dst;
    }

    /// <summary>音量バーが見る区間(0.1 秒)のサンプル数。</summary>
    public const int MeterWindow = SampleRate / 10;

    /// <summary>区間全体の RMS(空なら 0)。</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double acc = 0;
        foreach (var v in samples) acc += v * v;
        return Math.Sqrt(acc / samples.Length);
    }

    /// <summary><paramref name="end"/> の直前 <see cref="MeterWindow"/> サンプルの RMS(録音中の音量バーの値の元)。</summary>
    public static double TailRms(ReadOnlySpan<float> samples, int end)
    {
        end = Math.Clamp(end, 0, samples.Length);
        int start = Math.Max(0, end - MeterWindow);
        return Rms(samples[start..end]);
    }

    /// <summary>無音を詰めた後も声の前後に残す長さ(秒)。語頭・語尾の弱い音を削らないための余白。</summary>
    public const double SpeechPadSeconds = 0.4;

    /// <summary>
    /// 復号に渡す前に無音区間を詰める。声の区間(30 ms 区間の RMS が <see cref="VoiceLevel"/> 以上)から
    /// <see cref="SpeechPadSeconds"/> 以内のサンプルだけを残す(先頭・末尾の無音は余白まで削り、発話の間の長い無音は余白 2 つ分に縮める)。
    /// 無音が長いほど Whisper は音声に無い文(「ご視聴ありがとうございました」等)を作るので、長さによらず復号に無音を長く渡さない。
    /// 声が無ければ(最も大きい区間が <paramref name="threshold"/> 未満なら)元のまま返す(無音の取り消しは呼び出し側が先に判定する)。
    /// </summary>
    public static float[] TrimSilence(float[] samples, double threshold, double padSeconds = SpeechPadSeconds, int sampleRate = SampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000);
        int pad = (int)(sampleRate * padSeconds);
        if (PeakFrameRms(samples, sampleRate) < threshold) return samples;
        double voice = VoiceLevel(samples, threshold, sampleRate);
        var keep = new bool[samples.Length];
        bool any = false;
        for (int i = 0; i < samples.Length; i += frame)
        {
            int end = Math.Min(samples.Length, i + frame);
            if (Rms(samples.AsSpan(i, end - i)) < voice) continue;
            any = true;
            int a = Math.Max(0, i - pad), b = Math.Min(samples.Length, end + pad);
            for (int j = a; j < b; j++) keep[j] = true;
        }
        if (!any) return samples;
        int n = 0;
        foreach (var k in keep) if (k) n++;
        if (n == samples.Length) return samples;
        // whisper.cpp は 1 秒未満の入力を復号しない。詰めて 1 秒を割るなら後ろを無音で埋める(元の長さは超えない)
        var dst = new float[Math.Max(n, Math.Min(samples.Length, (int)(sampleRate * MinDecodeSeconds)))];
        for (int i = 0, o = 0; i < samples.Length; i++) if (keep[i]) dst[o++] = samples[i];
        return dst;
    }

    /// <summary>無音を詰めた後に残す最短の長さ(秒)。</summary>
    public const double MinDecodeSeconds = 1.1;

    /// <summary>声とみなす下限の、その録音で最も大きい 30 ms 区間の RMS に対する比(約 -32 dB)。</summary>
    public const double VoiceRelativeLevel = 0.025;

    /// <summary>声とみなす下限の、その録音の雑音の大きさ(静かな方から 1 割の 30 ms 区間の RMS)に対する倍率。</summary>
    public const double VoiceOverNoise = 3;

    /// <summary>
    /// <see cref="TrimSilence"/> が声とみなす 30 ms 区間の RMS の下限。録音の大きさに合わせる: 最も大きい区間の
    /// <see cref="VoiceRelativeLevel"/> 倍(小さい声・遠いマイクでも、声の弱い部分を無音として削らない)。ただし雑音の
    /// <see cref="VoiceOverNoise"/> 倍は下回らない(雑音を声として残さない)。どちらも <paramref name="threshold"/> は超えない(普通の大きさの録音の詰め方はほぼ変わらない)。
    /// </summary>
    public static double VoiceLevel(float[] samples, double threshold, int sampleRate = SampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000);
        var rms = new List<double>(samples.Length / frame + 1);
        for (int i = 0; i < samples.Length; i += frame)
            rms.Add(Rms(samples.AsSpan(i, Math.Min(samples.Length, i + frame) - i)));
        if (rms.Count == 0) return threshold;
        double peak = rms.Max();
        rms.Sort();
        double noise = rms[rms.Count / 10];
        return Math.Min(threshold, Math.Max(peak * VoiceRelativeLevel, noise * VoiceOverNoise));
    }

    /// <summary>30 ms 区間ごとの RMS の最大値。無音判定に使う。</summary>
    public static double PeakFrameRms(float[] samples, int sampleRate = SampleRate)
    {
        int frame = Math.Max(1, sampleRate * 30 / 1000);
        double best = 0;
        for (int i = 0; i < samples.Length; i += frame)
        {
            int end = Math.Min(samples.Length, i + frame);
            double acc = 0;
            for (int j = i; j < end; j++) acc += samples[j] * samples[j];
            best = Math.Max(best, Math.Sqrt(acc / (end - i)));
        }
        return best;
    }
}
