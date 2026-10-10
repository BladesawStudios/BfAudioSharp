namespace BfAudioSharp;

/// <summary>
/// Encodes 16-bit PCM to DSP-ADPCM, the codec of almost every Tears of the Kingdom sound. A port of the public
/// reference encoder (jackoalan/gc-dspadpcm-encode, from Nintendo's DSPADPCM tool): it works out the eight predictor
/// pairs for the whole channel, then picks the best predictor and scale for every 14-sample frame.
/// </summary>
public static class DspAdpcmEncoder
{
    public const int FrameSamples = 14;
    public const int FrameBytes = 8;

    /// <summary>Encodes one channel. Returns the 16 coefficients (8 pairs) and the frames, 8 bytes per 14 samples.</summary>
    public static (short[] Coefs, byte[] Data) Encode(ReadOnlySpan<short> samples)
    {
        int[] coefsFlat = CorrelateCoefs(samples);
        (int, int)[] pairs = new (int, int)[8];
        for (int i = 0; i < 8; i++) pairs[i] = (coefsFlat[i * 2], coefsFlat[i * 2 + 1]);

        int frames = (samples.Length + FrameSamples - 1) / FrameSamples;
        byte[] adpcm = new byte[frames * FrameBytes];

        // [history 2, history 1, s0 .. s13]; the frame encoder writes the decoded samples back so the next frame
        // starts from what the decoder will have, not from the source.
        int[] pcm = new int[16];
        int hist2 = 0, hist1 = 0;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * FrameSamples;
            int count = Math.Min(FrameSamples, samples.Length - baseIndex);
            pcm[0] = hist2;
            pcm[1] = hist1;
            for (int z = 0; z < FrameSamples; z++) pcm[z + 2] = z < count ? samples[baseIndex + z] : 0;
            EncodeFrame(pcm, count, pairs, adpcm.AsSpan(f * FrameBytes, FrameBytes));
            hist2 = pcm[count];
            hist1 = pcm[count + 1];
        }

        short[] coefs = new short[16];
        for (int i = 0; i < 16; i++) coefs[i] = (short)coefsFlat[i];
        return (coefs, adpcm);
    }

    // ---- predictor coefficients (DSPCorrelateCoefs) ----

    private static double[] InnerProductMerge(int[] pcm, int baseIndex)
    {
        double[] vec = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double acc = 0;
            for (int x = 0; x < 14; x++) acc -= (double)pcm[baseIndex + x - i] * pcm[baseIndex + x];
            vec[i] = acc;
        }
        return vec;
    }

    private static double[,] OuterProductMerge(int[] pcm, int baseIndex)
    {
        double[,] mtx = new double[3, 3];
        for (int x = 1; x < 3; x++)
            for (int y = 1; y < 3; y++)
            {
                double acc = 0;
                for (int z = 0; z < 14; z++) acc += (double)pcm[baseIndex + z - x] * pcm[baseIndex + z - y];
                mtx[x, y] = acc;
            }
        return mtx;
    }

    private static bool AnalyzeRanges(double[,] mtx, int[] vecIdxs)
    {
        double[] recips = new double[3];
        for (int x = 1; x < 3; x++)
        {
            double val = Math.Max(Math.Abs(mtx[x, 1]), Math.Abs(mtx[x, 2]));
            if (val < 2.2204460492503131e-16) return true;
            recips[x] = 1.0 / val;
        }

        int maxIndex = 0;
        for (int i = 1; i < 3; i++)
        {
            for (int x = 1; x < i; x++)
            {
                double tmp = mtx[x, i];
                for (int y = 1; y < x; y++) tmp -= mtx[x, y] * mtx[y, i];
                mtx[x, i] = tmp;
            }

            double val = 0.0;
            for (int x = i; x < 3; x++)
            {
                double tmp = mtx[x, i];
                for (int y = 1; y < i; y++) tmp -= mtx[x, y] * mtx[y, i];
                mtx[x, i] = tmp;
                tmp = Math.Abs(tmp) * recips[x];
                if (tmp >= val)
                {
                    val = tmp;
                    maxIndex = x;
                }
            }

            if (maxIndex != i)
            {
                for (int y = 1; y < 3; y++) (mtx[maxIndex, y], mtx[i, y]) = (mtx[i, y], mtx[maxIndex, y]);
                recips[maxIndex] = recips[i];
            }

            vecIdxs[i] = maxIndex;
            if (mtx[i, i] == 0.0) return true;

            if (i != 2)
            {
                double tmp = 1.0 / mtx[i, i];
                for (int x = i + 1; x < 3; x++) mtx[x, i] *= tmp;
            }
        }

        double vmin = 1.0e10, vmax = 0.0;
        for (int i = 1; i < 3; i++)
        {
            double tmp = Math.Abs(mtx[i, i]);
            vmin = Math.Min(vmin, tmp);
            vmax = Math.Max(vmax, tmp);
        }
        return vmin / vmax < 1.0e-10;
    }

    private static void BidirectionalFilter(double[,] mtx, int[] vecIdxs, double[] vec)
    {
        int x = 0;
        for (int i = 1; i < 3; i++)
        {
            int index = vecIdxs[i];
            double tmp = vec[index];
            vec[index] = vec[i];
            if (x != 0)
            {
                for (int y = x; y < i; y++) tmp -= vec[y] * mtx[i, y];
            }
            else if (tmp != 0.0)
            {
                x = i;
            }
            vec[i] = tmp;
        }

        for (int i = 2; i > 0; i--)
        {
            double tmp = vec[i];
            for (int y = i + 1; y < 3; y++) tmp -= vec[y] * mtx[i, y];
            vec[i] = tmp / mtx[i, i];
        }

        vec[0] = 1.0;
    }

    private static bool QuadraticMerge(double[] vec)
    {
        double v2 = vec[2];
        double tmp = 1.0 - v2 * v2;
        if (tmp == 0.0) return true;
        double v0 = (vec[0] - v2 * v2) / tmp;
        double v1 = (vec[1] - vec[1] * v2) / tmp;
        vec[0] = v0;
        vec[1] = v1;
        return Math.Abs(v1) > 1.0;
    }

    private static void FinishRecord(double[] vin, double[] output)
    {
        for (int z = 1; z < 3; z++)
        {
            if (vin[z] >= 1.0) vin[z] = 0.9999999999;
            else if (vin[z] <= -1.0) vin[z] = -0.9999999999;
        }
        output[0] = 1.0;
        output[1] = vin[2] * vin[1] + vin[1];
        output[2] = vin[2];
    }

    private static void MatrixFilter(double[] src, double[] dst)
    {
        double[,] mtx = new double[3, 3];
        mtx[2, 0] = 1.0;
        for (int i = 1; i < 3; i++) mtx[2, i] = -src[i];

        for (int i = 2; i > 0; i--)
        {
            double val = 1.0 - mtx[i, i] * mtx[i, i];
            for (int y = 1; y <= i; y++) mtx[i - 1, y] = (mtx[i, i] * mtx[i, y] + mtx[i, y]) / val;
        }

        dst[0] = 1.0;
        for (int i = 1; i < 3; i++)
        {
            dst[i] = 0.0;
            for (int y = 1; y <= i; y++) dst[i] += mtx[i, y] * dst[i - y];
        }
    }

    private static void MergeFinishRecord(double[] src, double[] dst)
    {
        double[] tmp = new double[3];
        double val = src[0];
        dst[0] = 1.0;
        for (int i = 1; i < 3; i++)
        {
            double v2 = 0.0;
            for (int y = 1; y < i; y++) v2 += dst[y] * src[i - y];
            dst[i] = val > 0.0 ? -(v2 + src[i]) / val : 0.0;
            tmp[i] = dst[i];
            for (int y = 1; y < i; y++) dst[y] += dst[i] * dst[i - y];
            val *= 1.0 - dst[i] * dst[i];
        }
        FinishRecord(tmp, dst);
    }

    private static double ContrastVectors(double[] s1, double[] s2)
    {
        double val = (s2[2] * s2[1] + -s2[1]) / (1.0 - s2[2] * s2[2]);
        double val1 = s1[0] * s1[0] + s1[1] * s1[1] + s1[2] * s1[2];
        double val2 = s1[0] * s1[1] + s1[1] * s1[2];
        double val3 = s1[0] * s1[2];
        return val1 + 2.0 * val * val2 + 2.0 * (-s2[1] * val + -s2[2]) * val3;
    }

    private static void FilterRecords(double[][] vecBest, int exp, List<double[]> records)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            double[][] bufferList = new double[exp][];
            for (int i = 0; i < exp; i++) bufferList[i] = new double[3];
            int[] buffer1 = new int[exp];

            foreach (double[] record in records)
            {
                int index = 0;
                double value = 1.0e30;
                for (int i = 0; i < exp; i++)
                {
                    double tempVal = ContrastVectors(vecBest[i], record);
                    if (tempVal < value)
                    {
                        value = tempVal;
                        index = i;
                    }
                }
                buffer1[index]++;
                double[] buffer2 = new double[3];
                MatrixFilter(record, buffer2);
                for (int i = 0; i < 3; i++) bufferList[index][i] += buffer2[i];
            }

            for (int i = 0; i < exp; i++)
                if (buffer1[i] > 0)
                    for (int y = 0; y < 3; y++) bufferList[i][y] /= buffer1[i];

            for (int i = 0; i < exp; i++) MergeFinishRecord(bufferList[i], vecBest[i]);
        }
    }

    private static int[] CorrelateCoefs(ReadOnlySpan<short> source)
    {
        List<double[]> records = [];
        int[] hist = new int[14];
        int[] pcmWindow = new int[28];

        int pos = 0, remaining = source.Length;
        while (remaining > 0)
        {
            int frameSamples = Math.Min(remaining, 0x3800);
            ReadOnlySpan<short> block = source.Slice(pos, frameSamples);
            pos += frameSamples;
            remaining -= frameSamples;

            int i = 0;
            while (i < frameSamples)
            {
                Array.Copy(hist, pcmWindow, 14);
                for (int z = 0; z < 14; z++) pcmWindow[14 + z] = i + z < block.Length ? block[i + z] : 0;
                i += 14;

                double[] vec1 = InnerProductMerge(pcmWindow, 14);
                if (Math.Abs(vec1[0]) > 10.0)
                {
                    double[,] mtx = OuterProductMerge(pcmWindow, 14);
                    int[] vecIdxs = new int[3];
                    if (!AnalyzeRanges(mtx, vecIdxs))
                    {
                        BidirectionalFilter(mtx, vecIdxs, vec1);
                        if (!QuadraticMerge(vec1))
                        {
                            double[] record = new double[3];
                            FinishRecord(vec1, record);
                            records.Add(record);
                        }
                    }
                }

                Array.Copy(pcmWindow, 14, hist, 0, 14);
            }
        }

        // Silence or degenerate input: trivial predictors.
        if (records.Count == 0) return new int[16];

        double[][] vecBest = new double[8][];
        for (int i = 0; i < 8; i++) vecBest[i] = new double[3];

        double[] vecSum = [1.0, 0.0, 0.0];
        foreach (double[] record in records)
        {
            MatrixFilter(record, vecBest[0]);
            for (int y = 1; y < 3; y++) vecSum[y] += vecBest[0][y];
        }
        for (int y = 1; y < 3; y++) vecSum[y] /= records.Count;
        MergeFinishRecord(vecSum, vecBest[0]);

        int exp = 1;
        for (int w = 0; w < 3; w++)
        {
            double[] vec2 = [0.0, -1.0, 0.0];
            for (int i = 0; i < exp; i++)
                for (int y = 0; y < 3; y++) vecBest[exp + i][y] = 0.01 * vec2[y] + vecBest[i][y];
            exp = 1 << (w + 1);
            FilterRecords(vecBest, exp, records);
        }

        int[] coefs = new int[16];
        int n = 0;
        for (int z = 0; z < 8; z++)
            for (int k = 1; k <= 2; k++)
            {
                double d = -vecBest[z][k] * 2048.0;
                int v;
                if (d > 0.0) v = d > 32767.0 ? 32767 : (int)Math.Round(d);
                else v = d < -32768.0 ? -32768 : (int)Math.Round(d);
                coefs[n++] = v;
            }
        return coefs;
    }

    // ---- one frame (DSPEncodeFrame) ----

    private static void EncodeFrame(int[] pcm, int sampleCount, (int, int)[] coefs, Span<byte> output)
    {
        int[][] inSamples = new int[8][];
        int[][] outSamples = new int[8][];
        int[] scale = new int[8];
        double[] distAccum = new double[8];

        for (int i = 0; i < 8; i++)
        {
            (int c0, int c1) = coefs[i];
            int[] ins = inSamples[i] = new int[16];
            int[] outs = outSamples[i] = new int[14];
            ins[0] = pcm[0];
            ins[1] = pcm[1];

            long distance = 0;
            for (int s = 0; s < sampleCount; s++)
            {
                long v1 = ((long)pcm[s] * c1 + (long)pcm[s + 1] * c0) / 2048;
                ins[s + 2] = (int)v1;
                long v2 = pcm[s + 2] - v1;
                long v3 = v2 >= 32767 ? 32767 : v2 <= -32768 ? -32768 : v2;
                if (Math.Abs(v3) > Math.Abs(distance)) distance = v3;
            }

            int sc = 0;
            while (sc <= 12 && (distance > 7 || distance < -8))
            {
                sc++;
                distance /= 2;
            }
            sc = sc <= 1 ? -1 : sc - 2;

            while (true)
            {
                sc++;
                distAccum[i] = 0.0;
                int index = 0;

                for (int s = 0; s < sampleCount; s++)
                {
                    long v1 = (long)ins[s] * c1 + (long)ins[s + 1] * c0;
                    long v2 = (((long)pcm[s + 2] << 11) - v1) / 2048;
                    double fv = v2 / (double)(1 << sc);
                    int v3 = v2 > 0 ? (int)(fv + 0.4999999) : (int)(fv - 0.4999999);

                    if (v3 < -8)
                    {
                        v3 = -8 - v3;
                        if (index < v3) index = v3;
                        v3 = -8;
                    }
                    else if (v3 > 7)
                    {
                        v3 -= 7;
                        if (index < v3) index = v3;
                        v3 = 7;
                    }

                    outs[s] = v3;

                    v1 = (v1 + (((long)v3 * (1 << sc)) << 11) + 1024) >> 11;
                    v2 = v1 >= 32767 ? 32767 : v1 <= -32768 ? -32768 : v1;
                    ins[s + 2] = (int)v2;
                    long diff = pcm[s + 2] - v2;
                    distAccum[i] += diff * (double)diff;
                }

                int x = index + 8;
                while (x > 256)
                {
                    sc++;
                    if (sc >= 12) sc = 11;
                    x >>= 1;
                }

                if (!(sc < 12 && index > 1)) break;
            }

            scale[i] = sc;
        }

        int best = 0;
        double vmin = distAccum[0];
        for (int i = 1; i < 8; i++)
        {
            if (distAccum[i] < vmin)
            {
                vmin = distAccum[i];
                best = i;
            }
        }

        int[] bestIns = inSamples[best];
        int[] bestOuts = outSamples[best];
        for (int s = 0; s < sampleCount; s++) pcm[s + 2] = bestIns[s + 2];
        for (int s = sampleCount; s < 14; s++) bestOuts[s] = 0;

        output[0] = (byte)((best << 4) | (scale[best] & 0xF));
        for (int y = 0; y < 7; y++) output[y + 1] = (byte)((bestOuts[y * 2] << 4) | (bestOuts[y * 2 + 1] & 0xF));
    }
}
