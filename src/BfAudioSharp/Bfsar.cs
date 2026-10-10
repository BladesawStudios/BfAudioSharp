namespace BfAudioSharp;

public sealed class SarStringNode
{
    public bool Leaf;
    public ushort Bit;
    public int Left, Right;
    public int StringId;
    public uint ItemId;
}

public sealed class SarOptionalParams
{
    public uint Flags;
    public uint[] Values = [];

    public static SarOptionalParams Read(NwReader r, int at)
    {
        var p = new SarOptionalParams { Flags = r.U32(at) };
        p.Values = new uint[System.Numerics.BitOperations.PopCount(p.Flags)];
        for (int i = 0; i < p.Values.Length; i++) p.Values[i] = r.U32(at + 4 + i * 4);
        return p;
    }

    public uint? Get(int bit)
    {
        if ((Flags & (1u << bit)) == 0) return null;
        return Values[System.Numerics.BitOperations.PopCount(Flags & ((1u << bit) - 1))];
    }

    public int Size => 4 + Values.Length * 4;
}

public sealed class SarSendValue
{
    public byte MainSend;
    public byte[] FxSend = [];

    public static SarSendValue Read(NwReader r, int at, int fx = 3) => new() { MainSend = r.U8(at), FxSend = r.D.Slice(at + 1, fx).ToArray() };
}

public sealed class SarStreamTrack
{
    public byte Volume, Pan, Span, Flags;
    public byte[] Channels = [];
    public SarSendValue? Send;
    public byte LpfFrequency, BiquadType, BiquadValue;
}

public sealed class SarStreamSoundInfo
{
    public ushort ValidTracks;
    public ushort ChannelCount;
    public List<SarStreamTrack> Tracks = [];
    public float Pitch;
    public SarSendValue? Send;
    public uint PrefetchFileId;
    public NwRef ExtensionRef;
}

public sealed class SarWaveSoundInfo
{
    public uint Index;
    public uint TrackCount;
    public SarOptionalParams Params = new();
}

public sealed class SarSequenceSoundInfo
{
    public uint[] BankIds = [];
    public uint AllocateTrackFlags;
    public SarOptionalParams Params = new();
}

public sealed class SarSoundInfo
{
    public uint FileId;
    public uint PlayerId;
    public byte Volume;
    public byte RemoteFilter;
    public ushort Padding;
    public ushort DetailType;
    public SarOptionalParams Params = new();
    public SarStreamSoundInfo? Stream;
    public SarWaveSoundInfo? Wave;
    public SarSequenceSoundInfo? Sequence;
}

public sealed class SarSoundGroupInfo
{
    public uint StartId, EndId;
    public uint[] FileIds = [];
    public uint[]? WaveArchiveIds;
    public SarOptionalParams Params = new();
}

public sealed class SarBankInfo
{
    public uint FileId;
    public uint[] WaveArchiveIds = [];
    public SarOptionalParams Params = new();
}

public sealed class SarWaveArchiveInfo
{
    public uint FileId;
    public bool LoadIndividual;
    public SarOptionalParams Params = new();
}

public sealed class SarGroupInfo
{
    public uint FileId;
    public SarOptionalParams Params = new();
}

public sealed class SarPlayerInfo
{
    public uint PlayableSoundMax;
    public SarOptionalParams Params = new();
}

public sealed class SarFileInfo
{
    public ushort LocationType;
    public NwSizedRef? Internal;
    public uint[] AttachedGroups = [];
    public string? ExternalPath;
    public byte[]? Data;
}

public sealed class SarPlayerLimits
{
    public ushort SequenceSoundMax, SequenceTrackMax, StreamSoundMax, StreamTrackMax, StreamChannelMax, WaveSoundMax, WaveTrackMax;
    public byte StreamBufferTimes;
    public uint Options;
}

public sealed class BfsarFile
{
    public NwHeader Header = new();
    public List<string> Strings = [];
    public int TreeRoot;
    public List<SarStringNode> Tree = [];
    public List<SarSoundInfo> Sounds = [];
    public List<SarSoundGroupInfo> SoundGroups = [];
    public List<SarBankInfo> Banks = [];
    public List<SarWaveArchiveInfo> WaveArchives = [];
    public List<SarGroupInfo> Groups = [];
    public List<SarPlayerInfo> Players = [];
    public List<SarFileInfo> Files = [];
    public SarPlayerLimits? PlayerLimits;

    public bool BigEndian => Header.BigEndian;

    public string? NameOf(uint? stringId) => stringId is { } s && s < Strings.Count ? Strings[(int)s] : null;

    public static BfsarFile FromBinary(ReadOnlySpan<byte> d)
    {
        var f = new BfsarFile { Header = NwHeader.Read(d) };
        if (f.Header.Magic != "FSAR") throw new InvalidDataException("not a BFSAR");
        var r = new NwReader(d, f.BigEndian);
        if (f.Header.Find(0x2000) is { } strg) f.ReadStrings(r, strg.Offset + 8);
        var file = f.Header.Find(0x2002);
        if (f.Header.Find(0x2001) is { } info) f.ReadInfo(r, info.Offset + 8, file is { } fb ? fb.Offset + 8 : -1);
        return f;
    }

    void ReadStrings(NwReader r, int b)
    {
        var st = r.Ref(b);
        var lt = r.Ref(b + 8);
        if (!st.IsNull)
        {
            int t = b + st.Offset, n = (int)r.U32(t);
            for (int i = 0; i < n; i++)
            {
                var sr = r.SizedRef(t + 4 + i * 12);
                Strings.Add(r.CStr(t + sr.Offset));
            }
        }
        if (!lt.IsNull)
        {
            int t = b + lt.Offset;
            TreeRoot = r.S32(t);
            int n = (int)r.U32(t + 4);
            for (int i = 0; i < n; i++)
            {
                int at = t + 8 + i * 20;
                Tree.Add(new SarStringNode { Leaf = r.U16(at) != 0, Bit = r.U16(at + 2), Left = r.S32(at + 4), Right = r.S32(at + 8), StringId = r.S32(at + 12), ItemId = r.U32(at + 16) });
            }
        }
    }

    static uint[] IdTable(NwReader r, int at)
    {
        var n = (int)r.U32(at);
        var res = new uint[n];
        for (int i = 0; i < n; i++) res[i] = r.U32(at + 4 + i * 4);
        return res;
    }

    void ReadInfo(NwReader r, int b, int fileBody)
    {
        var refs = new NwRef[8];
        for (int i = 0; i < 8; i++) refs[i] = r.Ref(b + i * 8);
        foreach (var rf in refs)
        {
            if (rf.IsNull) continue;
            int t = b + rf.Offset;
            switch (rf.Type)
            {
                case 0x2100: foreach (var e in r.RefTable(t)) Sounds.Add(ReadSound(r, t + e.Offset)); break;
                case 0x2104:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        var g = new SarSoundGroupInfo { StartId = r.U32(at), EndId = r.U32(at + 4) };
                        var ft = r.Ref(at + 8);
                        if (!ft.IsNull) g.FileIds = IdTable(r, at + ft.Offset);
                        var wr = r.Ref(at + 16);
                        if (!wr.IsNull)
                        {
                            var wt = r.Ref(at + wr.Offset);
                            if (!wt.IsNull) g.WaveArchiveIds = IdTable(r, at + wr.Offset + wt.Offset);
                        }
                        g.Params = SarOptionalParams.Read(r, at + 24);
                        SoundGroups.Add(g);
                    }
                    break;
                case 0x2101:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        var k = new SarBankInfo { FileId = r.U32(at) };
                        var wt = r.Ref(at + 4);
                        if (!wt.IsNull) k.WaveArchiveIds = IdTable(r, at + wt.Offset);
                        k.Params = SarOptionalParams.Read(r, at + 12);
                        Banks.Add(k);
                    }
                    break;
                case 0x2103:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        WaveArchives.Add(new SarWaveArchiveInfo { FileId = r.U32(at), LoadIndividual = r.U32(at + 4) != 0, Params = SarOptionalParams.Read(r, at + 8) });
                    }
                    break;
                case 0x2105:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        Groups.Add(new SarGroupInfo { FileId = r.U32(at), Params = SarOptionalParams.Read(r, at + 4) });
                    }
                    break;
                case 0x2102:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        Players.Add(new SarPlayerInfo { PlayableSoundMax = r.U32(at), Params = SarOptionalParams.Read(r, at + 4) });
                    }
                    break;
                case 0x2106:
                    foreach (var e in r.RefTable(t))
                    {
                        int at = t + e.Offset;
                        var loc = r.Ref(at);
                        var fi = new SarFileInfo { LocationType = loc.Type };
                        if (!loc.IsNull)
                        {
                            int la = at + loc.Offset;
                            if (loc.Type == 0x220C)
                            {
                                var sr = r.SizedRef(la);
                                fi.Internal = sr;
                                var gt = r.Ref(la + 12);
                                if (!gt.IsNull) fi.AttachedGroups = IdTable(r, la + gt.Offset);
                                if (!sr.IsNull && fileBody >= 0 && fileBody + sr.Offset + sr.Size <= r.D.Length)
                                    fi.Data = r.D.Slice(fileBody + sr.Offset, (int)sr.Size).ToArray();
                            }
                            else if (loc.Type == 0x220D) fi.ExternalPath = r.CStr(la);
                        }
                        Files.Add(fi);
                    }
                    break;
                case 0x220B:
                    PlayerLimits = new SarPlayerLimits
                    {
                        SequenceSoundMax = r.U16(t), SequenceTrackMax = r.U16(t + 2), StreamSoundMax = r.U16(t + 4), StreamTrackMax = r.U16(t + 6),
                        StreamChannelMax = r.U16(t + 8), WaveSoundMax = r.U16(t + 10), WaveTrackMax = r.U16(t + 12), StreamBufferTimes = r.U8(t + 14),
                        Options = r.U32(t + 16)
                    };
                    break;
            }
        }
    }

    static SarSoundInfo ReadSound(NwReader r, int at)
    {
        var s = new SarSoundInfo { FileId = r.U32(at), PlayerId = r.U32(at + 4), Volume = r.U8(at + 8), RemoteFilter = r.U8(at + 9), Padding = r.U16(at + 10) };
        var dr = r.Ref(at + 12);
        s.DetailType = dr.Type;
        s.Params = SarOptionalParams.Read(r, at + 20);
        if (dr.IsNull) return s;
        int d = at + dr.Offset;
        switch (dr.Type)
        {
            case 0x2201:
            {
                var st = new SarStreamSoundInfo { ValidTracks = r.U16(d), ChannelCount = r.U16(d + 2), Pitch = r.F32(d + 12) };
                var tt = r.Ref(d + 4);
                if (!tt.IsNull)
                {
                    int tb = d + tt.Offset;
                    foreach (var e in r.RefTable(tb))
                    {
                        int ta = tb + e.Offset;
                        var tr = new SarStreamTrack { Volume = r.U8(ta), Pan = r.U8(ta + 1), Span = r.U8(ta + 2), Flags = r.U8(ta + 3) };
                        var ct = r.Ref(ta + 4);
                        if (!ct.IsNull) tr.Channels = r.D.Slice(ta + ct.Offset + 4, (int)r.U32(ta + ct.Offset)).ToArray();
                        var sv = r.Ref(ta + 12);
                        if (!sv.IsNull) tr.Send = SarSendValue.Read(r, ta + sv.Offset);
                        tr.LpfFrequency = r.U8(ta + 20);
                        tr.BiquadType = r.U8(ta + 21);
                        tr.BiquadValue = r.U8(ta + 22);
                        st.Tracks.Add(tr);
                    }
                }
                var sr = r.Ref(d + 16);
                if (!sr.IsNull) st.Send = SarSendValue.Read(r, d + sr.Offset);
                st.ExtensionRef = r.Ref(d + 24);
                st.PrefetchFileId = r.U32(d + 32);
                s.Stream = st;
                break;
            }
            case 0x2202:
                s.Wave = new SarWaveSoundInfo { Index = r.U32(d), TrackCount = r.U32(d + 4), Params = SarOptionalParams.Read(r, d + 8) };
                break;
            case 0x2203:
            {
                var q = new SarSequenceSoundInfo();
                var bt = r.Ref(d);
                if (!bt.IsNull) q.BankIds = IdTable(r, d + bt.Offset);
                q.AllocateTrackFlags = r.U32(d + 8);
                q.Params = SarOptionalParams.Read(r, d + 12);
                s.Sequence = q;
                break;
            }
        }
        return s;
    }
}

public sealed class BfwarFile
{
    public NwHeader Header = new();
    public List<byte[]> Waves = [];

    public static BfwarFile FromBinary(ReadOnlySpan<byte> d)
    {
        var f = new BfwarFile { Header = NwHeader.Read(d) };
        if (f.Header.Magic != "FWAR") throw new InvalidDataException("not a BFWAR");
        var r = new NwReader(d, f.Header.BigEndian);
        var info = f.Header.Find(0x6800) ?? throw new InvalidDataException("BFWAR without INFO");
        var file = f.Header.Find(0x6801) ?? throw new InvalidDataException("BFWAR without FILE");
        int b = info.Offset + 8, n = (int)r.U32(b);
        for (int i = 0; i < n; i++)
        {
            var sr = r.SizedRef(b + 4 + i * 12);
            f.Waves.Add(sr.IsNull ? [] : d.Slice(file.Offset + 8 + sr.Offset, (int)sr.Size).ToArray());
        }
        return f;
    }
}

public sealed class BfgrpEntry
{
    public uint FileId;
    public NwSizedRef Location;
    public byte[]? Data;
}

public sealed class BfgrpFile
{
    public NwHeader Header = new();
    public List<BfgrpEntry> Entries = [];
    public List<(uint ItemId, uint LoadFlags)> Dependencies = [];

    public static BfgrpFile FromBinary(ReadOnlySpan<byte> d)
    {
        var f = new BfgrpFile { Header = NwHeader.Read(d) };
        if (f.Header.Magic != "FGRP") throw new InvalidDataException("not a BFGRP");
        var r = new NwReader(d, f.Header.BigEndian);
        var file = f.Header.Find(0x7801);
        if (f.Header.Find(0x7800) is { } info)
        {
            int b = info.Offset + 8;
            foreach (var e in r.RefTable(b))
            {
                int at = b + e.Offset;
                var g = new BfgrpEntry { FileId = r.U32(at), Location = r.SizedRef(at + 4) };
                if (!g.Location.IsNull && file is { } fb && g.Location.Size > 0)
                    g.Data = d.Slice(fb.Offset + 8 + g.Location.Offset, (int)g.Location.Size).ToArray();
                f.Entries.Add(g);
            }
        }
        if (f.Header.Find(0x7802) is { } infx)
        {
            int b = infx.Offset + 8;
            foreach (var e in r.RefTable(b))
                f.Dependencies.Add((r.U32(b + e.Offset), r.U32(b + e.Offset + 4)));
        }
        return f;
    }
}
