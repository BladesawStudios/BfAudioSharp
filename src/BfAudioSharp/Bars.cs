namespace BfAudioSharp;

public sealed class AmtaStreamTrack
{
    public uint ChannelCount;
    public float Volume;
}

public sealed class AmtaData
{
    public uint NameOffset;
    public uint Unknown04;
    public byte Type;
    public byte WaveChannels;
    public byte UsedStreamTracks;
    public byte Flags;
    public float Volume;
    public uint SampleRate;
    public uint LoopStart;
    public uint LoopEnd;
    public float Loudness;
    public AmtaStreamTrack[] StreamTracks = [];
    public float? AmplitudePeak;
}

public sealed class AmtaMarker
{
    public uint Id;
    public uint NameOffset;
    public string Name = "";
    public uint Start;
    public uint Length;
}

public sealed class AmtaExtEntry
{
    public uint NameOffset;
    public string Name = "";
    public uint Value;
}

public sealed class AmtaFile
{
    public bool BigEndian;
    public ushort Version;
    public uint Size;
    public AmtaData Data = new();
    public List<AmtaMarker> Markers = [];
    public List<AmtaExtEntry> Ext = [];
    public byte[] StringTable = [];
    public string Name = "";

    public static AmtaFile FromBinary(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x1C || d[0] != 'A' || d[1] != 'M' || d[2] != 'T' || d[3] != 'A') throw new InvalidDataException("not an AMTA");
        var big = NwReader.IsBig(d, 4);
        var r = new NwReader(d, big);
        var a = new AmtaFile { BigEndian = big, Version = r.U16(6), Size = r.U32(8) };
        int data = r.S32(12), mark = r.S32(16), ext = r.S32(20), strg = r.S32(24);
        if (strg > 0 && strg + 8 <= d.Length)
        {
            int n = (int)Math.Min(r.U32(strg + 4), (uint)(d.Length - strg - 8));
            a.StringTable = d.Slice(strg + 8, n).ToArray();
        }
        string Str(uint off) => off < a.StringTable.Length ? new NwReader(a.StringTable, big).CStr((int)off) : "";

        if (data > 0)
        {
            int size = (int)r.U32(data + 4), b = data + 8;
            var x = a.Data;
            x.NameOffset = r.U32(b);
            x.Unknown04 = r.U32(b + 4);
            x.Type = r.U8(b + 8);
            x.WaveChannels = r.U8(b + 9);
            x.UsedStreamTracks = r.U8(b + 10);
            x.Flags = r.U8(b + 11);
            x.Volume = r.F32(b + 12);
            x.SampleRate = r.U32(b + 16);
            x.LoopStart = r.U32(b + 20);
            x.LoopEnd = r.U32(b + 24);
            x.Loudness = r.F32(b + 28);
            int tracks = Math.Clamp((size - 0x20) / 8, 0, 8);
            x.StreamTracks = new AmtaStreamTrack[tracks];
            for (int i = 0; i < tracks; i++)
                x.StreamTracks[i] = new AmtaStreamTrack { ChannelCount = r.U32(b + 0x20 + i * 8), Volume = r.F32(b + 0x24 + i * 8) };
            if (size >= 0x64) x.AmplitudePeak = r.F32(b + 0x60);
            a.Name = Str(x.NameOffset);
        }
        if (mark > 0)
        {
            int n = (int)r.U32(mark + 8);
            for (int i = 0; i < n; i++)
            {
                int at = mark + 12 + i * 16;
                var m = new AmtaMarker { Id = r.U32(at), NameOffset = r.U32(at + 4), Start = r.U32(at + 8), Length = r.U32(at + 12) };
                m.Name = Str(m.NameOffset);
                a.Markers.Add(m);
            }
        }
        if (ext > 0)
        {
            int n = (int)r.U32(ext + 8);
            for (int i = 0; i < n; i++)
            {
                int at = ext + 12 + i * 8;
                var e = new AmtaExtEntry { NameOffset = r.U32(at), Value = r.U32(at + 4) };
                e.Name = Str(e.NameOffset);
                a.Ext.Add(e);
            }
        }
        return a;
    }
}

public sealed class BarsEntry
{
    public uint Hash;
    public uint AmtaOffset;
    public uint AudioOffset;
    public AmtaFile Amta = new();
    public byte[]? Audio;

    public string Name => Amta.Name;
    public string? AudioMagic => Audio is { Length: >= 4 } a ? System.Text.Encoding.ASCII.GetString(a, 0, 4) : null;
}

public sealed class BarsFile
{
    public bool BigEndian;
    public ushort Version;
    public uint FileSize;
    public List<BarsEntry> Entries = [];

    public static BarsFile FromBinary(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x10 || d[0] != 'B' || d[1] != 'A' || d[2] != 'R' || d[3] != 'S') throw new InvalidDataException("not a BARS");
        var big = NwReader.IsBig(d, 8);
        var r = new NwReader(d, big);
        var f = new BarsFile { BigEndian = big, FileSize = r.U32(4), Version = r.U16(10) };
        int n = (int)r.U32(12);
        for (int i = 0; i < n; i++)
        {
            var e = new BarsEntry { Hash = r.U32(16 + i * 4) };
            int at = 16 + n * 4 + i * 8;
            e.AmtaOffset = r.U32(at);
            e.AudioOffset = r.U32(at + 4);
            if (e.AmtaOffset != uint.MaxValue)
            {
                var am = d[(int)e.AmtaOffset..];
                var size = (int)new NwReader(am, NwReader.IsBig(am, 4)).U32(8);
                e.Amta = AmtaFile.FromBinary(am[..Math.Min(size, am.Length)]);
            }
            if (e.AudioOffset != uint.MaxValue && e.AudioOffset + 0x10 <= d.Length)
            {
                var au = d[(int)e.AudioOffset..];
                e.Audio = au[..Math.Min(NwHeader.Length(au), au.Length)].ToArray();
            }
            f.Entries.Add(e);
        }
        return f;
    }
}
