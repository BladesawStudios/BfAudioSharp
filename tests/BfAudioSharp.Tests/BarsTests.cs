using System.Buffers.Binary;
using System.Text;

namespace BfAudioSharp.Tests;

public class BarsTests
{
    private static short[] Tone(int samples) => [.. Enumerable.Range(0, samples).Select(i => (short)(7000 * Math.Sin(i / 11.0)))];

    // An AMTA block in the layout of version 5: a 0x24 byte header, the DATA blob whose first word is its size, then the name.
    private static byte[] Amta(string name, int channels, float volumeDb)
    {
        byte[] amta = new byte[0x100];
        "AMTA"u8.CopyTo(amta);
        BinaryPrimitives.WriteUInt16LittleEndian(amta.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(amta.AsSpan(6), 0x0500);
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0x10), 0x80); // DATA
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0x14), 0xA0); // MARK
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0x24), 0x40);
        amta[0x31] = (byte)channels;
        Encoding.UTF8.GetBytes(name).CopyTo(amta, 0x24 + 0x40);
        BinaryPrimitives.WriteSingleLittleEndian(amta.AsSpan(0x80 + 0xC), volumeDb);
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0xA0), 0); // no markers
        return amta;
    }

    // BARS: header, hashes, pairs, AMTAs, then each BWAV at a 0x40 boundary. A null BWAV means stream-only.
    private static byte[] Bars(params (string Name, byte[]? Bwav)[] entries)
    {
        int count = entries.Length;
        int amtaStart = 0x10 + count * 12;
        int position = amtaStart + count * 0x100;
        int[] bwavAt = new int[count];
        List<byte> region = [];
        for (int i = 0; i < count; i++)
        {
            if (entries[i].Bwav is not { } bwav) { bwavAt[i] = -1; continue; }
            int pad = (position + 0x3F) / 0x40 * 0x40 - position;
            region.AddRange(new byte[pad]);
            position += pad;
            bwavAt[i] = position;
            region.AddRange(bwav);
            position += bwav.Length;
        }

        byte[] file = new byte[amtaStart + count * 0x100 + region.Count];
        "BARS"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 0x0102);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10 + i * 4), (uint)(1000 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10 + count * 4 + i * 8), (uint)(amtaStart + i * 0x100));
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x10 + count * 4 + i * 8 + 4), bwavAt[i]);
            Amta(entries[i].Name, 1, -6.5f).CopyTo(file, amtaStart + i * 0x100);
        }
        region.CopyTo(file, amtaStart + count * 0x100);
        return file;
    }

    [Fact]
    public void ABarsListsItsEntries()
    {
        byte[] bars = Bars(("First", Bwav.Build([Tone(300)], 32000)), ("Second", null));

        TotkBars parsed = TotkBars.Parse(bars);

        Assert.Equal(["First", "Second"], parsed.Entries.Select(e => e.Name));
        Assert.Equal([1000u, 1001u], parsed.Entries.Select(e => e.NameHash));
        Assert.NotEqual(-1, parsed.Entries[0].BwavOffset);
        Assert.Equal(-1, parsed.Entries[1].BwavOffset);
        Assert.Equal(1, parsed.Entries[0].Metadata.ChannelCount);
        Assert.Equal(-6.5f, parsed.Entries[0].Metadata.VolumeDb);
        Assert.Equal(Math.Pow(10, -6.5 / 20), parsed.Entries[0].Metadata.VolumeLinear, 6);
    }

    [Fact]
    public void AnEmptyPlaceholderBwavIsNotCountedAsEmbedded()
    {
        byte[] placeholder = Bwav.Build([Tone(300)], 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(placeholder.AsSpan(0x1C), 0);
        byte[] bars = Bars(("Only", placeholder));

        Assert.Equal(-1, TotkBars.Parse(bars).Entries[0].BwavOffset);
        Assert.NotEqual(-1, TotkBars.RawBwavOffset(bars, 0));
    }

    [Fact]
    public void RebuildingWithNothingReplacedGivesTheSameBytes()
    {
        byte[] bars = Bars(("A", Bwav.Build([Tone(300)], 32000)), ("B", null), ("C", Bwav.Build([Tone(800)], 32000)));

        Assert.Equal(bars, TotkBars.Rebuild(bars, new Dictionary<int, byte[]>()));
    }

    [Fact]
    public void ReplacingOneBwavMovesTheOnesAfterItAndKeepsTheRest()
    {
        byte[] first = Bwav.Build([Tone(300)], 32000), second = Bwav.Build([Tone(800)], 32000);
        byte[] bars = Bars(("A", first), ("B", second));
        byte[] longer = Bwav.Build([Tone(5000)], 32000);

        byte[] rebuilt = TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = longer });

        TotkBars parsed = TotkBars.Parse(rebuilt);
        Assert.Equal(longer, rebuilt.AsSpan(parsed.Entries[0].BwavOffset, longer.Length).ToArray());
        Assert.Equal(0, parsed.Entries[1].BwavOffset % 0x40);
        Assert.Equal(second, rebuilt.AsSpan(parsed.Entries[1].BwavOffset, second.Length).ToArray());
        Assert.Equal((uint)rebuilt.Length, BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(4)));
        // The AMTA blocks, which sit between the pair table and the BWAV region, are untouched.
        int amtaStart = 0x10 + 2 * 12, regionStart = TotkBars.Parse(bars).Entries[0].BwavOffset;
        Assert.Equal(bars[amtaStart..regionStart], rebuilt[amtaStart..regionStart]);
    }

    [Fact]
    public void ReplacingAStreamOnlyEntryOrABadIndexIsRefused()
    {
        byte[] bars = Bars(("A", Bwav.Build([Tone(300)], 32000)), ("B", null));
        byte[] bwav = Bwav.Build([Tone(100)], 32000);

        Assert.Throws<InvalidDataException>(() => TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [1] = bwav }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [5] = bwav }));
        Assert.Throws<InvalidDataException>(() => TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = new byte[40] }));
    }

    [Fact]
    public void ThingsThatAreNotBarsAreRefused()
        => Assert.Throws<InvalidDataException>(() => TotkBars.Parse(new byte[64]));
}
