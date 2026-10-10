using System.Buffers.Binary;
using System.Text;

namespace BfAudioSharp;

public enum NwCodec : byte { Pcm8 = 0, Pcm16 = 1, DspAdpcm = 2, ImaAdpcm = 3 }

public readonly record struct NwRef(ushort Type, int Offset)
{
    public bool IsNull => Offset == -1;
}

public readonly record struct NwSizedRef(ushort Type, int Offset, uint Size)
{
    public bool IsNull => Offset == -1;
}

public readonly ref struct NwReader
{
    public readonly ReadOnlySpan<byte> D;
    public readonly bool Big;

    public NwReader(ReadOnlySpan<byte> d, bool big) { D = d; Big = big; }

    public static bool IsBig(ReadOnlySpan<byte> d, int bomAt) => d[bomAt] == 0xFE && d[bomAt + 1] == 0xFF;

    public byte U8(int at) => D[at];
    public ushort U16(int at) => Big ? BinaryPrimitives.ReadUInt16BigEndian(D[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(D[at..]);
    public short S16(int at) => (short)U16(at);
    public uint U32(int at) => Big ? BinaryPrimitives.ReadUInt32BigEndian(D[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(D[at..]);
    public int S32(int at) => (int)U32(at);
    public float F32(int at) => BitConverter.Int32BitsToSingle(S32(at));
    public NwRef Ref(int at) => new(U16(at), S32(at + 4));
    public NwSizedRef SizedRef(int at) => new(U16(at), S32(at + 4), U32(at + 8));
    public string Magic(int at) => Encoding.ASCII.GetString(D.Slice(at, 4));

    public string CStr(int at)
    {
        if (at < 0 || at >= D.Length) return "";
        int e = D[at..].IndexOf((byte)0);
        return Encoding.UTF8.GetString(e < 0 ? D[at..] : D.Slice(at, e));
    }

    public NwRef[] RefTable(int at)
    {
        var n = (int)U32(at);
        var res = new NwRef[n];
        for (int i = 0; i < n; i++) res[i] = Ref(at + 4 + i * 8);
        return res;
    }
}

public readonly record struct NwBlock(ushort Type, int Offset, int Size);

public sealed class NwHeader
{
    public string Magic = "";
    public bool BigEndian;
    public ushort HeaderSize;
    public uint Version;
    public uint FileSize;
    public List<NwBlock> Blocks = [];

    public string VersionString => $"{Version >> 24}.{(Version >> 16) & 0xFF}.{(Version >> 8) & 0xFF}.{Version & 0xFF}";

    public static NwHeader Read(ReadOnlySpan<byte> d)
    {
        var big = NwReader.IsBig(d, 4);
        var r = new NwReader(d, big);
        var h = new NwHeader { Magic = r.Magic(0), BigEndian = big, HeaderSize = r.U16(6), Version = r.U32(8), FileSize = r.U32(12) };
        int n = r.U16(16);
        for (int i = 0; i < n; i++)
        {
            var at = 0x14 + i * 12;
            h.Blocks.Add(new NwBlock(r.U16(at), r.S32(at + 4), (int)r.U32(at + 8)));
        }
        return h;
    }

    public NwBlock? Find(ushort type)
    {
        foreach (var b in Blocks)
            if (b.Type == type && b.Offset != -1) return b;
        return null;
    }

    public static int Length(ReadOnlySpan<byte> d) => (int)new NwReader(d, NwReader.IsBig(d, 4)).U32(12);
}

public readonly record struct DspContext(ushort PredScale, short Yn1, short Yn2);

public sealed class DspAdpcmInfo
{
    public short[] Coefs = new short[16];
    public DspContext Context;
    public DspContext LoopContext;
    public ushort Padding;

    public static DspAdpcmInfo Read(NwReader r, int at)
    {
        var i = new DspAdpcmInfo();
        for (int k = 0; k < 16; k++) i.Coefs[k] = r.S16(at + k * 2);
        i.Context = new DspContext(r.U16(at + 0x20), r.S16(at + 0x22), r.S16(at + 0x24));
        i.LoopContext = new DspContext(r.U16(at + 0x26), r.S16(at + 0x28), r.S16(at + 0x2A));
        if (at + 0x2E <= r.D.Length) i.Padding = r.U16(at + 0x2C);
        return i;
    }
}

public sealed class ImaAdpcmInfo
{
    public short Predictor, LoopPredictor;
    public byte StepIndex, LoopStepIndex;

    public static ImaAdpcmInfo Read(NwReader r, int at) => new()
    {
        Predictor = r.S16(at), StepIndex = r.U8(at + 2), LoopPredictor = r.S16(at + 4), LoopStepIndex = r.U8(at + 6)
    };
}

public static class Dsp
{
    public const int FrameSamples = 14;
    public const int FrameBytes = 8;

    public static long Frames(long samples) => (samples + FrameSamples - 1) / FrameSamples;

    public static long ByteLength(long samples)
    {
        long full = samples / FrameSamples, rem = samples % FrameSamples;
        return full * FrameBytes + (rem == 0 ? 0 : 1 + (rem + 1) / 2);
    }

    public static long SamplesOf(long bytes) => bytes / FrameBytes * FrameSamples + Math.Max(0, (bytes % FrameBytes - 1) * 2);

    public static void Decode(ReadOnlySpan<byte> data, ReadOnlySpan<short> coefs, short yn1, short yn2, Span<short> output)
    {
        int h1 = yn1, h2 = yn2, n = output.Length, o = 0;
        for (int f = 0; o < n && f * FrameBytes < data.Length; f++)
        {
            var at = f * FrameBytes;
            int ps = data[at], scale = 1 << (ps & 0xF), idx = (ps >> 4) & 7;
            int c1 = coefs[idx * 2], c2 = coefs[idx * 2 + 1];
            for (int s = 0; s < FrameSamples && o < n; s++)
            {
                var bi = at + 1 + (s >> 1);
                if (bi >= data.Length) { output[o..].Clear(); return; }
                int nib = (s & 1) == 0 ? data[bi] >> 4 : data[bi] & 0xF;
                if (nib >= 8) nib -= 16;
                int v = ((nib * scale) << 11) + 1024 + c1 * h1 + c2 * h2 >> 11;
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                output[o++] = (short)v;
                h2 = h1;
                h1 = v;
            }
        }
        if (o < n) output[o..].Clear();
    }

    public static void Decode(NwCodec codec, ReadOnlySpan<byte> data, DspAdpcmInfo? dsp, Span<short> output)
    {
        switch (codec)
        {
            case NwCodec.DspAdpcm:
                if (dsp is null) throw new InvalidDataException("DSP channel without ADPCM info");
                Decode(data, dsp.Coefs, dsp.Context.Yn1, dsp.Context.Yn2, output);
                break;
            case NwCodec.Pcm16:
                var n = Math.Min(output.Length, data.Length / 2);
                for (int i = 0; i < n; i++) output[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
                output[n..].Clear();
                break;
            case NwCodec.Pcm8:
                var m = Math.Min(output.Length, data.Length);
                for (int i = 0; i < m; i++) output[i] = (short)((sbyte)data[i] << 8);
                output[m..].Clear();
                break;
            default: throw new NotSupportedException($"codec {codec}");
        }
    }
}

public static class NwAudio
{
    public static long DataLength(NwCodec codec, long samples) => codec switch
    {
        NwCodec.Pcm8 => samples,
        NwCodec.Pcm16 => samples * 2,
        NwCodec.DspAdpcm => Dsp.ByteLength(samples),
        NwCodec.ImaAdpcm => (samples + 1) / 2,
        _ => samples
    };

    public static void CopyCanonical(NwCodec codec, bool big, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        src.CopyTo(dst);
        if (codec == NwCodec.Pcm16 && big)
            for (int i = 0; i + 1 < dst.Length; i += 2) (dst[i], dst[i + 1]) = (dst[i + 1], dst[i]);
    }
}
