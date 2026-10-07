namespace Voice2Txt.Core;

/// <summary>
/// 録音中のオーバーレイの音量バー(MOC の .meter、5 本)。録れている音の大きさに連動させ、無音なら低く揃えて止める。
/// 値の流れ: 録音(<see cref="IRecording.InputRms"/>)→ <see cref="Level"/>(0〜1)→ <see cref="Follow"/>(表示の減衰)→ <see cref="BarHeights"/>。
/// </summary>
public static class InputMeter
{
    /// <summary>これ以下(dBFS)は 0(マイクの地の雑音より下)。</summary>
    public const double FloorDb = -60;

    /// <summary>これ以上(dBFS)は 1(普通の声の大きさ)。</summary>
    public const double FullDb = -15;

    public const int BarCount = 5, MinBarHeight = 3, MaxBarHeight = 14;

    /// <summary>1 回の描画(約 90 ms)で下がる量。上がるときはすぐ追う。</summary>
    public const double FallPerTick = 0.15;

    private static readonly double[] Shape = [0.55, 0.8, 1.0, 0.8, 0.55];

    /// <summary>RMS(0〜1)を音量バーの値(0〜1)にする。dB で FloorDb〜FullDb を 0〜1 に割り当てる。</summary>
    public static double Level(double rms)
    {
        if (!(rms > 0)) return 0;
        double db = 20 * Math.Log10(rms);
        return Math.Clamp((db - FloorDb) / (FullDb - FloorDb), 0, 1);
    }

    /// <summary>表示中の値を新しい値へ寄せる。上がるときはすぐ、下がるときは 1 回に <paramref name="fall"/> まで。</summary>
    public static double Follow(double shown, double target, double fall = FallPerTick) =>
        target >= shown ? target : Math.Max(target, shown - fall);

    /// <summary>5 本のバーの高さ(px)。値が 0 なら全部 <see cref="MinBarHeight"/> で揃う。<paramref name="tick"/> は値に比例した揺れだけに使う。</summary>
    public static int[] BarHeights(double level, int tick)
    {
        level = Math.Clamp(level, 0, 1);
        var h = new int[BarCount];
        for (int i = 0; i < BarCount; i++)
        {
            double wobble = 0.7 + 0.3 * Math.Abs(Math.Sin(tick * 0.6 + i * 0.9));
            h[i] = MinBarHeight + (int)Math.Round((MaxBarHeight - MinBarHeight) * level * Shape[i] * wobble);
        }
        return h;
    }
}
