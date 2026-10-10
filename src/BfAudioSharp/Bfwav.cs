namespace BfAudioSharp;

public sealed class WaveChannel
{
    public byte[] Data = [];
    public DspAdpcmInfo? Dsp;
    public ImaAdpcmInfo? Ima;
    public uint Reserved;
}

public sealed class BfwavFile
{
    public NwHeader Header = new();
    public NwCodec Codec;
    public bool Loop;
    public ushort Padding;
    public uint SampleRate;
    public uint LoopStart;
    public uint SampleCount;
    public uint? OriginalLoopStart;
    public List<WaveChannel> Channels = [];

    public bool BigEndian => Header.BigEndian;

    public static BfwavFile FromBinary(ReadOnlySpan<byte> d)
    {
        var h = NwHeader.Read(d);
        if (h.Magic != "FWAV") throw new InvalidDataException("not a BFWAV");
        var r = new NwReader(d, h.BigEndian);
        var info = h.Find(0x7000) ?? throw new InvalidDataException("BFWAV without INFO");
        var data = h.Find(0x7001) ?? throw new InvalidDataException("BFWAV without DATA");
        int b = info.Offset + 8;
        var w = new BfwavFile
        {
            Header = h, Codec = (NwCodec)r.U8(b), Loop = r.U8(b + 1) != 0, Padding = r.U16(b + 2),
            SampleRate = r.U32(b + 4), LoopStart = r.U32(b + 8), SampleCount = r.U32(b + 12)
        };
        int table = b + 0x10;
        if (h.Version >= 0x00010100) { w.OriginalLoopStart = r.U32(b + 0x10); table = b + 0x14; }
        var len = NwAudio.DataLength(w.Codec, w.SampleCount);
        foreach (var cref in r.RefTable(table))
        {
            int ci = table + cref.Offset;
            var sref = r.Ref(ci);
            var aref = r.Ref(ci + 8);
            var ch = new WaveChannel { Reserved = r.U32(ci + 16) };
            if (!aref.IsNull)
            {
                if (aref.Type == 0x0300) ch.Dsp = DspAdpcmInfo.Read(r, ci + aref.Offset);
                else if (aref.Type == 0x0301) ch.Ima = ImaAdpcmInfo.Read(r, ci + aref.Offset);
            }
            int start = data.Offset + 8 + sref.Offset;
            int n = (int)Math.Min(len, d.Length - start);
            ch.Data = new byte[n];
            NwAudio.CopyCanonical(w.Codec, h.BigEndian, d.Slice(start, n), ch.Data);
            w.Channels.Add(ch);
        }
        return w;
    }
}
