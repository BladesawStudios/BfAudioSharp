using System.Buffers.Binary;

namespace BfAudioSharp;

public sealed record LoudnessStats(double Peak, double RmsMax, double MomentaryMax, double Integrated, double LoudnessRange);

/// <summary>
/// The loudness figures stored in the version 5 AMTA blocks of Tears of the Kingdom. After a channel map, a DATA section
/// holds a flags word (usually 0x4F) and five float32 values: sample peak (0..1, four decimals), the loudest 400 ms RMS
/// window (approximate), momentary-max loudness (EBU R128, LUFS), integrated loudness (LUFS) and loudness range (LU; zero
/// under 3 s). Silent audio stores peak 0 and -96 for both loudnesses. Against the vanilla romfs the peak, momentary-max and
/// integrated values agree to about 0.1 dB for audio of 3 s or more; short effects differ by a few dB, which is close enough
/// for a replacement.
/// </summary>
public static class AmtaLoudness
{
    public const double FloorDb = -96.0;

    private static (double[] Shelf, double[] HighPass) KWeighting(int sampleRate)
    {
        double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double vh = Math.Pow(10.0, gain / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        double[] shelf =
        [
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0,
        ];
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / sampleRate);
        a0 = 1.0 + k / q + k * k;
        double[] highPass = [2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0];
        return (shelf, highPass);
    }

    private static double Lufs(double energy)
        => energy <= 0.0 ? FloorDb : Math.Max(FloorDb, -0.691 + 10.0 * Math.Log10(energy));

    // Linear-interpolated percentile (numpy's default method).
    private static double Percentile(List<double> sorted, double pct)
    {
        double pos = (sorted.Count - 1) * pct / 100.0;
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static List<double> WindowMeans(List<double> blocks, int width)
    {
        List<double> means = [];
        for (int i = 0; i + width <= blocks.Count; i++)
        {
            double sum = 0;
            for (int k = 0; k < width; k++) sum += blocks[i + k];
            means.Add(sum / width);
        }
        return means;
    }

    /// <summary>Measures 16-bit audio the way the AMTA values are stored.</summary>
    public static LoudnessStats Measure(short[][] channels, int sampleRate)
    {
        int samples = channels.Length == 0 ? 0 : channels[0].Length;
        if (samples == 0 || sampleRate <= 0) return new LoudnessStats(0.0, 0.0, FloorDb, FloorDb, 0.0);

        var (shelf, highPass) = KWeighting(sampleRate);
        double b0 = shelf[0], b1 = shelf[1], b2 = shelf[2], a1 = shelf[3], a2 = shelf[4], c1 = highPass[0], c2 = highPass[1];
        int block = Math.Max(1, sampleRate / 10);
        int blocks = (samples + block - 1) / block;
        List<double> kEnergy = [.. new double[blocks]];
        List<double> rawEnergy = [.. new double[blocks]];
        int peak = 0;
        const double scale = 1.0 / 32768.0;

        foreach (short[] channel in channels)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, w1 = 0, w2 = 0;
            for (int bi = 0; bi < blocks; bi++)
            {
                double kAcc = 0.0, rawAcc = 0.0;
                int start = bi * block, end = Math.Min(samples, (bi + 1) * block);
                for (int i = start; i < end; i++)
                {
                    int v = channel[i];
                    if (v > peak) peak = v;
                    else if (-v > peak) peak = -v;
                    double x = v * scale;
                    rawAcc += x * x;
                    double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                    double w = y - 2.0 * y1 + y2 - c1 * w1 - c2 * w2;
                    x2 = x1; x1 = x; y2 = y1; y1 = y; w2 = w1; w1 = w;
                    kAcc += w * w;
                }
                kEnergy[bi] += kAcc / block;
                rawEnergy[bi] += rawAcc / block / channels.Length;
            }
        }

        // Clips shorter than one 400 ms window are measured as if padded with silence.
        while (kEnergy.Count < 4)
        {
            kEnergy.Add(0.0);
            rawEnergy.Add(0.0);
        }

        List<double> momentary = WindowMeans(kEnergy, 4);
        double momentaryMax = Lufs(momentary.Max());

        double integrated = FloorDb;
        List<double> gated = [.. momentary.Where(e => Lufs(e) > -70.0)];
        if (gated.Count > 0)
        {
            double relative = Lufs(gated.Sum() / gated.Count) - 10.0;
            gated = [.. gated.Where(e => Lufs(e) > relative)];
            if (gated.Count > 0) integrated = Lufs(gated.Sum() / gated.Count);
        }

        double loudnessRange = 0.0;
        if (kEnergy.Count >= 30)
        {
            List<double> shortTerm = [.. WindowMeans(kEnergy, 30).Where(e => Lufs(e) > -70.0)];
            if (shortTerm.Count > 0)
            {
                double relative = Lufs(shortTerm.Sum() / shortTerm.Count) - 20.0;
                List<double> levels = [.. shortTerm.Select(Lufs).Where(l => l > relative).Order()];
                if (levels.Count > 0) loudnessRange = Percentile(levels, 95) - Percentile(levels, 10);
            }
        }

        double rmsMax = Math.Sqrt(WindowMeans(rawEnergy, 4).Max());

        // The peak is a whole number over 32768, so rounding it exactly as a decimal matches Python's round().
        double peakRounded = (double)Math.Round(Math.Min(peak / 32768m, 1m), 4, MidpointRounding.ToEven);
        return new LoudnessStats(peakRounded, Math.Round(Math.Min(rmsMax, 1.0), 4, MidpointRounding.ToEven), momentaryMax, integrated, loudnessRange);
    }

    /// <summary>
    /// The offset of the flags word before the five loudness floats in an AMTA, or null. The channel map before it varies in
    /// length, so this scans for a flags word (0x4F, 0x5F, ... in the vanilla game) followed by plausible values.
    /// </summary>
    public static int? LocateBlock(ReadOnlySpan<byte> data, int amtaOffset)
    {
        if (amtaOffset < 0 || amtaOffset + 0x28 > data.Length || !data.Slice(amtaOffset, 4).SequenceEqual("AMTA"u8)) return null;
        if (((BinaryPrimitives.ReadUInt32LittleEndian(data[(amtaOffset + 4)..]) >> 24) & 0xFF) < 5) return null;

        int end = amtaOffset + 0x24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(amtaOffset + 0x24)..]);
        for (int p = amtaOffset + 0x34; p < end - 0x13 && p + 0x14 <= data.Length; p += 4)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            if (flags >= 0x40 && flags < 0x100 && (flags & 0xF) == 0xF
                && BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..])) is >= 0.0f and <= 1.0001f
                && BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 0xC)..])) <= 0.0f
                && BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 0x10)..])) <= 0.0f)
                return p;
        }
        return null;
    }

    /// <summary>Overwrites the loudness floats of an AMTA in place (no sizes change). False if it has no recognisable block.</summary>
    public static bool Write(Span<byte> data, int amtaOffset, LoudnessStats stats)
    {
        if (LocateBlock(data, amtaOffset) is not { } p) return false;
        double[] values = [stats.Peak, stats.RmsMax, stats.MomentaryMax, stats.Integrated, stats.LoudnessRange];
        for (int i = 0; i < 5; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data[(p + 4 + i * 4)..], BitConverter.SingleToUInt32Bits((float)values[i]));
        return true;
    }
}
