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
