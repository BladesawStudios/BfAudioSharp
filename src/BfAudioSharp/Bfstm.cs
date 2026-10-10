namespace BfAudioSharp;

public sealed class StreamInfo
{
    public NwCodec Codec;
    public bool Loop;
    public byte ChannelCount;
    public byte RegionCount;
    public uint SampleRate;
    public uint LoopStart;
    public uint SampleCount;
    public uint BlockCount;
    public uint BlockSize;
    public uint BlockSamples;
    public uint LastBlockSize;
    public uint LastBlockSamples;
    public uint LastBlockPaddedSize;
    public uint SeekSize;
    public uint SeekInterval;
    public NwRef SampleData;
    public ushort? RegionInfoSize;
    public NwRef? RegionRef;
    public uint? OriginalLoopStart;
    public uint? OriginalLoopEnd;
    public uint? Crc32;
    public int Size;
}

public sealed class StreamTrack
{
    public byte Volume, Pan, Span, Flags;
    public byte[] Channels = [];
}

public sealed class StreamChannel
{
    public DspAdpcmInfo? Dsp;
    public ImaAdpcmInfo? Ima;
    public byte[] Data = [];
}

public sealed class StreamRegion
{
    public uint Start, End;
    public DspContext[] Contexts = [];
}

public sealed class PrefetchData
{
    public uint StartFrame;
    public uint PrefetchSize;
    public uint Reserved;
    public uint Samples;
}

public abstract class StreamFileBase
{
    public NwHeader Header = new();
    public StreamInfo Info = new();
    public List<StreamTrack>? Tracks;
    public List<StreamChannel> Channels = [];

    public bool BigEndian => Header.BigEndian;

    private protected void ReadInfo(NwReader r)
    {
        var blk = Header.Find(0x4000) ?? throw new InvalidDataException("stream without INFO");
        int b = blk.Offset + 8, end = blk.Offset + blk.Size;
        var siRef = r.Ref(b);
        var trRef = r.Ref(b + 8);
        var chRef = r.Ref(b + 16);
        int si = b + siRef.Offset;
        int next = end;
        if (!trRef.IsNull && b + trRef.Offset > si) next = Math.Min(next, b + trRef.Offset);
        if (!chRef.IsNull && b + chRef.Offset > si) next = Math.Min(next, b + chRef.Offset);
        var i = Info;
        i.Size = next - si;
        i.Codec = (NwCodec)r.U8(si);
        i.Loop = r.U8(si + 1) != 0;
        i.ChannelCount = r.U8(si + 2);
        i.RegionCount = r.U8(si + 3);
        i.SampleRate = r.U32(si + 4);
        i.LoopStart = r.U32(si + 8);
        i.SampleCount = r.U32(si + 0xC);
        i.BlockCount = r.U32(si + 0x10);
        i.BlockSize = r.U32(si + 0x14);
        i.BlockSamples = r.U32(si + 0x18);
        i.LastBlockSize = r.U32(si + 0x1C);
        i.LastBlockSamples = r.U32(si + 0x20);
        i.LastBlockPaddedSize = r.U32(si + 0x24);
        i.SeekSize = r.U32(si + 0x28);
        i.SeekInterval = r.U32(si + 0x2C);
        i.SampleData = r.Ref(si + 0x30);
        if (i.Size >= 0x44) { i.RegionInfoSize = r.U16(si + 0x38); i.RegionRef = r.Ref(si + 0x3C); }
        if (i.Size >= 0x4C) { i.OriginalLoopStart = r.U32(si + 0x44); i.OriginalLoopEnd = r.U32(si + 0x48); }
        if (i.Size >= 0x50) i.Crc32 = r.U32(si + 0x4C);

        if (!trRef.IsNull)
        {
            Tracks = [];
            int tt = b + trRef.Offset;
            foreach (var t in r.RefTable(tt))
            {
                int at = tt + t.Offset;
                var tr = new StreamTrack { Volume = r.U8(at), Pan = r.U8(at + 1), Span = r.U8(at + 2), Flags = r.U8(at + 3) };
                var cr = r.Ref(at + 4);
                if (!cr.IsNull)
                {
                    int ct = at + cr.Offset;
                    tr.Channels = r.D.Slice(ct + 4, (int)r.U32(ct)).ToArray();
                }
                Tracks.Add(tr);
            }
        }
        if (!chRef.IsNull)
        {
            int ct = b + chRef.Offset;
            foreach (var c in r.RefTable(ct))
            {
                int at = ct + c.Offset;
                var ch = new StreamChannel();
                var a = r.Ref(at);
                if (!a.IsNull)
                {
                    if (a.Type == 0x0300) ch.Dsp = DspAdpcmInfo.Read(r, at + a.Offset);
                    else if (a.Type == 0x0301) ch.Ima = ImaAdpcmInfo.Read(r, at + a.Offset);
                }
                Channels.Add(ch);
            }
        }
    }

    private protected void Deinterleave(ReadOnlySpan<byte> src, uint firstBlock, int maxBytes, long maxSamples)
    {
        var i = Info;
        int ch = Channels.Count;
        if (ch == 0) return;
        var outs = new byte[ch][];
        long per = 0;
        int pos = 0;
        for (uint blk = firstBlock; blk < i.BlockCount && pos < maxBytes; blk++)
        {
            bool last = blk == i.BlockCount - 1;
            per += last ? i.LastBlockSize : i.BlockSize;
            pos += (int)((last ? i.LastBlockPaddedSize : i.BlockSize) * ch);
        }
        long need = NwAudio.DataLength(i.Codec, maxSamples);
        per = Math.Min(per, need);
        for (int c = 0; c < ch; c++) outs[c] = new byte[per];
        pos = 0;
        long w = 0;
        for (uint blk = firstBlock; blk < i.BlockCount && w < per; blk++)
        {
            bool last = blk == i.BlockCount - 1;
            int stride = (int)(last ? i.LastBlockPaddedSize : i.BlockSize);
            int valid = (int)Math.Min(last ? i.LastBlockSize : i.BlockSize, per - w);
            for (int c = 0; c < ch; c++)
            {
                int at = pos + c * stride;
                int n = Math.Max(0, Math.Min(valid, src.Length - at));
                if (n > 0) NwAudio.CopyCanonical(i.Codec, BigEndian, src.Slice(at, n), outs[c].AsSpan((int)w, n));
            }
            pos += stride * ch;
            w += valid;
        }
        for (int c = 0; c < ch; c++) Channels[c].Data = outs[c];
    }
}

public sealed class BfstmFile : StreamFileBase
{
    public List<StreamRegion>? Regions;
    public byte[]? SeekTable;

    public (short Yn1, short Yn2) SeekHistory(int block, int channel)
    {
        int at = (block * Channels.Count + channel) * 4;
        if (SeekTable is null || at + 4 > SeekTable.Length) return (0, 0);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(SeekTable.AsSpan(at)), System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(SeekTable.AsSpan(at + 2)));
    }

    public static BfstmFile FromBinary(ReadOnlySpan<byte> d)
    {
        var f = new BfstmFile { Header = NwHeader.Read(d) };
        if (f.Header.Magic != "FSTM") throw new InvalidDataException("not a BFSTM");
        var r = new NwReader(d, f.BigEndian);
        f.ReadInfo(r);
        var i = f.Info;
        if (f.Header.Find(0x4001) is { } seek)
            f.SeekTable = d.Slice(seek.Offset + 8, Math.Min(seek.Size - 8, (int)(i.BlockCount * i.SeekSize * f.Channels.Count))).ToArray();
        if (f.Header.Find(0x4003) is { } regn && i.RegionRef is { IsNull: false } rr && i.RegionInfoSize is { } rs)
        {
            f.Regions = [];
            for (int k = 0; k < i.RegionCount; k++)
            {
                int at = regn.Offset + 8 + rr.Offset + k * rs;
                var reg = new StreamRegion { Start = r.U32(at), End = r.U32(at + 4), Contexts = new DspContext[f.Channels.Count] };
                for (int c = 0; c < f.Channels.Count; c++)
                {
                    int ca = at + 8 + c * 6;
                    reg.Contexts[c] = new DspContext(r.U16(ca), r.S16(ca + 2), r.S16(ca + 4));
                }
                f.Regions.Add(reg);
            }
        }
        var data = f.Header.Find(0x4002) ?? throw new InvalidDataException("BFSTM without DATA");
        int start = data.Offset + 8 + i.SampleData.Offset;
        f.Deinterleave(d[start..], 0, int.MaxValue, i.SampleCount);
        return f;
    }
}

public sealed class BfstpFile : StreamFileBase
{
    public List<PrefetchData> Prefetch = [];

    public static BfstpFile FromBinary(ReadOnlySpan<byte> d)
    {
        var f = new BfstpFile { Header = NwHeader.Read(d) };
        if (f.Header.Magic != "FSTP") throw new InvalidDataException("not a BFSTP");
        var r = new NwReader(d, f.BigEndian);
        f.ReadInfo(r);
        var pdat = f.Header.Find(0x4004) ?? throw new InvalidDataException("BFSTP without PDAT");
        int b = pdat.Offset + 8;
        int n = (int)r.U32(b);
        for (int k = 0; k < n; k++)
        {
            int at = b + 4 + k * 0x14;
            var p = new PrefetchData { StartFrame = r.U32(at), PrefetchSize = r.U32(at + 4), Reserved = r.U32(at + 8) };
            var dr = r.Ref(at + 12);
            f.Prefetch.Add(p);
            if (k != 0) continue;
            var i = f.Info;
            uint first = i.BlockSamples == 0 ? 0 : p.StartFrame / i.BlockSamples;
            long samples = 0;
            int pos = 0;
            for (uint blk = first; blk < i.BlockCount && pos < p.PrefetchSize; blk++)
            {
                bool last = blk == i.BlockCount - 1;
                samples += last ? i.LastBlockSamples : i.BlockSamples;
                pos += (int)((last ? i.LastBlockPaddedSize : i.BlockSize) * f.Channels.Count);
            }
            p.Samples = (uint)Math.Min(samples, i.SampleCount - p.StartFrame);
            int start = at + dr.Offset;
            f.Deinterleave(d.Slice(start, Math.Min((int)p.PrefetchSize, d.Length - start)), first, (int)p.PrefetchSize, p.Samples);
        }
        return f;
    }
}
