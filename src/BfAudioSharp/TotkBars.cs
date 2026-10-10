using System.Buffers.Binary;
using System.Text;

namespace BfAudioSharp;

public sealed class TotkAmtaMarker
{
    public uint Id;
    public string Name = "";
    public uint Start;
    public uint Length;
}

public sealed class TotkAmtaTrack
{
    public uint ChannelCount;
    public float Volume;
}

/// <summary>What the AMTA block of a BARS entry says about its sound. The version 5 blocks of Tears of the Kingdom hold far less than older ones.</summary>
public sealed class TotkAmta
{
    public string Name = "";
    /// <summary>"Wave" or "Stream".</summary>
    public string AudioType = "Stream";
    public uint SampleRate;
    public int ChannelCount;
    public uint SampleCount;
    public uint LoopStart;
    public bool IsLooped;
    /// <summary>The volume as stored, in dB.</summary>
    public float VolumeDb;
    public double VolumeLinear = 1.0;
    public List<TotkAmtaTrack> StreamTracks = [];
    public List<TotkAmtaMarker> Markers = [];
    public float? AmplitudePeak;
}

public sealed class TotkBarsEntry
{
    public string Name = "";
    public uint NameHash;
    /// <summary>Offset of the AMTA block in the file.</summary>
    public int AmtaOffset;
    /// <summary>Offset of the embedded BWAV, or -1 if there isn't a playable one (none, or an empty placeholder).</summary>
    public int BwavOffset = -1;
    public TotkAmta Metadata = new();
}

/// <summary>
/// A BARS file as Tears of the Kingdom ships them (little endian, version 1.2): a header, the CRC32 hashes of the asset
/// names, a table of (AMTA offset, BWAV offset) pairs in the same order, the AMTA blocks, and then the BWAV blocks, each
/// aligned to 0x40. An asset whose sound streams from <c>Sound/Resource/Stream</c> keeps only a short prefetch BWAV here, or none.
/// </summary>
public sealed class TotkBars
{
    public List<TotkBarsEntry> Entries = [];

    private static uint U32(ReadOnlySpan<byte> d, int at)
        => at < 0 || at + 4 > d.Length ? throw new InvalidDataException($"The BARS file ends before offset 0x{at:x}.") : BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);

    private static int S32(ReadOnlySpan<byte> d, int at) => (int)U32(d, at);

    private static float F32(ReadOnlySpan<byte> d, int at) => BitConverter.UInt32BitsToSingle(U32(d, at));

    private static string CString(ReadOnlySpan<byte> d, int at)
    {
        if (at < 0 || at >= d.Length) throw new InvalidDataException($"The BARS file ends before offset 0x{at:x}.");
        int end = d[at..].IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("substring not found");
        return Encoding.UTF8.GetString(d.Slice(at, end));
    }

    public static bool IsBars(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("BARS"u8);

    public static TotkBars Parse(ReadOnlySpan<byte> data)
    {
        if (!IsBars(data)) throw new InvalidDataException("Not a BARS file");

        int count = (int)U32(data, 0xC);
        int hashes = 0x10, offsets = hashes + count * 4;
        TotkBars bars = new();
        for (int i = 0; i < count; i++)
        {
            int amta = (int)U32(data, offsets + i * 8);
            int bwavRaw = S32(data, offsets + i * 8 + 4);
            int bwav = bwavRaw is -1 or 0 ? -1 : bwavRaw;
            if (bwav != -1)
            {
                if (bwav + 14 > data.Length) bwav = -1;
                else if (!Bwav.IsBwav(data[bwav..])) bwav = -1;
                else if (Bwav.IsDummy(data[bwav..])) bwav = -1;
            }

            TotkAmta metadata = ParseAmta(data, amta);
            bars.Entries.Add(new TotkBarsEntry
            {
                Name = metadata.Name,
                NameHash = U32(data, hashes + i * 4),
                AmtaOffset = amta,
                BwavOffset = bwav,
                Metadata = metadata,
            });
        }
        return bars;
    }

    private static TotkAmta ParseAmta(ReadOnlySpan<byte> data, int amta)
    {
        if (amta < 0 || amta + 4 > data.Length || !data.Slice(amta, 4).SequenceEqual("AMTA"u8))
            throw new InvalidDataException($"Expected AMTA magic at offset 0x{amta:x}");

        int version = BinaryPrimitives.ReadUInt16LittleEndian(data[(amta + 6)..]);
        int major = (version >> 8) & 0xFF;

        if (major >= 5)
        {
            // Version 5 (the game's): the DATA blob is a 0x24 byte header, then its own size, then the asset name after it.
            uint dataRel = U32(data, amta + 0x10), markRel = U32(data, amta + 0x14);
            TotkAmta meta = new() { ChannelCount = amta + 0x31 < data.Length ? data[amta + 0x31] : 0 };

            if (markRel != 0)
            {
                int mark = amta + (int)markRel;
                uint markerCount = U32(data, mark);
                for (int i = 0; i < markerCount; i++)
                {
                    int at = mark + 4 + i * 16;
                    meta.Markers.Add(new TotkAmtaMarker
                    {
                        Id = U32(data, at),
                        Name = CString(data, at + 4 + (int)U32(data, at + 4)),
                        Start = U32(data, at + 8),
                        Length = U32(data, at + 12),
                    });
                }
            }

            try
            {
                int blob = (int)U32(data, amta + 0x24);
                meta.Name = CString(data, amta + 0x24 + blob);
            }
            catch (InvalidDataException)
            {
                meta.Name = "";
            }

            if (dataRel != 0)
            {
                meta.VolumeDb = F32(data, amta + (int)dataRel + 0xC);
                meta.VolumeLinear = meta.VolumeDb > -100 ? Math.Pow(10.0, meta.VolumeDb / 20.0) : 0.0;
            }
            return meta;
        }

        int dataOffset = amta + (int)U32(data, amta + 0xC);
        uint markOffsetRel = U32(data, amta + 0x10);
        uint stringsRel = major >= 3 ? U32(data, amta + 0x18) : U32(data, amta + 0x14);
        if (stringsRel == 0) throw new InvalidDataException($"AMTA at 0x{amta:x}: STRG section offset is zero - cannot read asset name");

        int strings = amta + (int)stringsRel + 8;
        int body = dataOffset + 8;
        byte flags = data[body + 0xB];
        TotkAmta m = new()
        {
            Name = CString(data, strings + (int)U32(data, body)),
            AudioType = data[body + 8] == 1 ? "Stream" : "Wave",
            ChannelCount = data[body + 9],
            SampleRate = U32(data, body + 0x10),
            LoopStart = U32(data, body + 0x14),
            SampleCount = U32(data, body + 0x18),
            VolumeDb = F32(data, body + 0x1C),
            IsLooped = (flags & 0x4) != 0,
        };
        m.VolumeLinear = Math.Pow(10.0, m.VolumeDb / 20.0);
        int tracks = Math.Min(data[body + 0xA], (byte)8);
        for (int i = 0; i < tracks; i++)
            m.StreamTracks.Add(new TotkAmtaTrack { ChannelCount = U32(data, body + 0x20 + i * 8), Volume = F32(data, body + 0x24 + i * 8) });
        if (major >= 4) m.AmplitudePeak = F32(data, body + 0x60);

        if (markOffsetRel != 0)
        {
            int mark = amta + (int)markOffsetRel + 8;
            uint markerCount = U32(data, mark);
            for (int i = 0; i < markerCount; i++)
            {
                int at = mark + 4 + i * 16;
                m.Markers.Add(new TotkAmtaMarker
                {
                    Id = U32(data, at),
                    Name = CString(data, strings + (int)U32(data, at + 4)),
                    Start = U32(data, at + 8),
                    Length = U32(data, at + 12),
                });
            }
        }
        return m;
    }

    /// <summary>
    /// The embedded BWAV bytes of an entry (everything from its offset on, as the BWAV is the last thing in its block and
    /// a reader only takes what its header says), or null if it has none.
    /// </summary>
    public static ReadOnlySpan<byte> EmbeddedBwav(ReadOnlySpan<byte> data, TotkBarsEntry entry)
        => entry.BwavOffset < 0 ? default : data[entry.BwavOffset..];

    /// <summary>
    /// Rebuilds the file with the embedded BWAV of the entries in <paramref name="replacements"/> (entry index to new BWAV)
    /// swapped. Everything before the BWAV region keeps its place. Blocks are sized by the distance to the next one, so
    /// padding the original tool left is kept, and with nothing replaced the output is the input byte for byte.
    /// </summary>
    public static byte[] Rebuild(ReadOnlySpan<byte> data, IReadOnlyDictionary<int, byte[]> replacements)
    {
        if (!IsBars(data)) throw new InvalidDataException("Not a BARS file");

        int count = (int)U32(data, 0xC);
        int pairs = 0x10 + count * 4;
        int[] bwavOffsets = new int[count];
        for (int i = 0; i < count; i++) bwavOffsets[i] = S32(data, pairs + i * 8 + 4);

        foreach (var (index, blob) in replacements)
        {
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(replacements), $"Entry index {index} out of range (BARS has {count} entries)");
            if (!Bwav.IsBwav(blob)) throw new InvalidDataException($"Replacement for entry {index} is not a BWAV");
            if (bwavOffsets[index] is -1 or 0)
                throw new InvalidDataException($"Entry {index} has no embedded BWAV to replace (stream-only entry; replace the romfs .bwav instead)");
        }

        int[] order = [.. bwavOffsets.Where(o => o is not (-1 or 0)).Distinct().Order()];
        if (order.Length == 0)
            return replacements.Count == 0 ? data.ToArray() : throw new InvalidDataException("BARS has no embedded BWAV blocks to replace");

        int regionStart = order[0];
        Dictionary<int, byte[]> blobs = [];
        for (int i = 0; i < order.Length; i++)
        {
            int next = i + 1 < order.Length ? order[i + 1] : data.Length;
            if (!data.Slice(order[i], 4).SequenceEqual("BWAV"u8)) throw new InvalidDataException($"No BWAV magic at offset 0x{order[i]:x}");
            blobs[order[i]] = data[order[i]..next].ToArray();
        }

        // Entries that shared an original block share the new one.
        foreach (var (index, blob) in replacements) blobs[bwavOffsets[index]] = blob;

        // Untouched blocks keep their original (already aligned, padding-included) bytes; new ones get zero padding to 0x40.
        Dictionary<int, int> newOffsets = [];
        List<byte> region = [];
        foreach (int offset in order)
        {
            int pad = (regionStart + region.Count + 0x3F) / 0x40 * 0x40 - (regionStart + region.Count);
            region.AddRange(new byte[pad]);
            newOffsets[offset] = regionStart + region.Count;
            region.AddRange(blobs[offset]);
        }

        byte[] output = new byte[regionStart + region.Count];
        data[..regionStart].CopyTo(output);
        region.CopyTo(output, regionStart);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)output.Length);
        for (int i = 0; i < count; i++)
            if (bwavOffsets[i] is not (-1 or 0))
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(pairs + i * 8 + 4), newOffsets[bwavOffsets[i]]);
        return output;
    }

    /// <summary>The raw BWAV offset of an entry in the pair table (-1 or 0 when it has none), even for an empty placeholder.</summary>
    public static int RawBwavOffset(ReadOnlySpan<byte> data, int entryIndex)
    {
        int count = (int)U32(data, 0xC);
        return S32(data, 0x10 + count * 4 + entryIndex * 8 + 4);
    }
}
