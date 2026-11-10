using System.Buffers.Binary;
using Concentus;

namespace BfAudioSharp.Tests;

public class StreamedEntryTests
{
    private const int Header = 0x10, Channel = 0x4C;

    private static short[] Tone(int samples, double period, double amplitude = 9000)
        => [.. Enumerable.Range(0, samples).Select(i => (short)(amplitude * Math.Sin(i / period)))];

    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
    private static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));

    private static double Correlation(short[] x, short[] y, int offset = 0)
    {
        double xy = 0, xx = 0, yy = 0;
        int n = Math.Min(x.Length, y.Length - offset);
        for (int i = 0; i < n; i++) { xy += (double)x[i] * y[i + offset]; xx += (double)x[i] * x[i]; yy += (double)y[i + offset] * y[i + offset]; }
        return xy / Math.Sqrt(xx * yy);
    }

    [Fact]
    public void AStubIsTheHeaderWithNoSamplesInTheFile()
    {
        byte[] full = Bwav.Build([Tone(20000, 17), Tone(20000, 23)], 48000, 100, 19000);
        byte[] stub = Bwav.MakeStub(full);

        Assert.Equal(Header + 2 * Channel, stub.Length);
        Assert.False(Bwav.IsPrefetch(stub));
        Assert.True(Bwav.IsDummy(stub));
        Assert.Equal(0, Bwav.SamplesInFile(stub));
        Assert.Equal(U32(full, 8), U32(stub, 8));
        for (int c = 0; c < 2; c++)
        {
            int at = Header + c * Channel;
            Assert.Equal(20000u, U32(stub, at + 8));
            Assert.Equal(0u, U32(stub, at + 0x0C));
            Assert.Equal(U32(full, at + 0x30), U32(stub, at + 0x30));
            Assert.Equal(0xC0u, U32(stub, at + 0x34));
            Assert.Equal(full.AsSpan(at + 0x10, 0x20).ToArray(), stub.AsSpan(at + 0x10, 0x20).ToArray());
        }
        Assert.Equal(Bwav.Loop(full), Bwav.Loop(stub));
    }

    [Fact]
    public void AStubReplacesAStubInABarsWithoutGrowingIt()
    {
        byte[] stream = Bwav.Build([Tone(30000, 11), Tone(30000, 13)], 48000);
        byte[] old = Bwav.MakeStub(Bwav.Build([Tone(500, 9), Tone(500, 9)], 48000));
        byte[] bars = BarsTests.Bars(("A", old), ("B", old));

        byte[] rebuilt = TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = Bwav.MakeStub(stream) });

        Assert.Equal(bars.Length, rebuilt.Length);
        int offset = TotkBars.RawBwavOffset(rebuilt, 0);
        Assert.Equal(30000, Bwav.SampleCount(rebuilt.AsSpan(offset)));
        Assert.True(Bwav.IsDummy(rebuilt.AsSpan(offset)));
    }

    private static (byte[] Bars, byte[] Shared, byte[] Other) SharedBars()
    {
        byte[] shared = Bwav.MakeStub(Bwav.Build([Tone(500, 9), Tone(500, 9)], 48000));
        byte[] other = Bwav.MakeStub(Bwav.Build([Tone(700, 5)], 48000));
        byte[] bars = BarsTests.Bars(("A", shared), ("B", null), ("C", other));
        int pairs = 0x10 + 3 * 4;
        BinaryPrimitives.WriteInt32LittleEndian(bars.AsSpan(pairs + 8 + 4), TotkBars.RawBwavOffset(bars, 0));
        return (bars, shared, other);
    }

    private static byte[] Block(byte[] bars, int entry) => TotkBars.BlockBytes(bars, entry)!;

    [Fact]
    public void AStreamedEntryLeavesABlockItSharesToTheOthers()
    {
        var (bars, shared, other) = SharedBars();
        byte[] mine = Bwav.MakeStub(Bwav.Build([Tone(30000, 11), Tone(30000, 13)], 44100));
        Assert.True(TotkBars.SharesBlock(bars, 0));

        byte[] rebuilt = TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = mine }, new HashSet<int> { 0 });

        Assert.Equal(30000, Bwav.SampleCount(rebuilt.AsSpan(TotkBars.RawBwavOffset(rebuilt, 0))));
        Assert.Equal(500, Bwav.SampleCount(rebuilt.AsSpan(TotkBars.RawBwavOffset(rebuilt, 1))));
        Assert.Equal(shared, Block(rebuilt, 1)[..shared.Length]);
        Assert.Equal(other, Block(rebuilt, 2)[..other.Length]);
        Assert.False(TotkBars.SharesBlock(rebuilt, 0));
        Assert.False(TotkBars.SharesBlock(rebuilt, 1));
        Assert.All(new[] { 0, 1, 2 }, i => Assert.Equal(0, TotkBars.RawBwavOffset(rebuilt, i) % 0x40));
        Assert.Equal(bars.Length + 0xC0, rebuilt.Length);
    }

    [Fact]
    public void WithoutOwnBlocksSharedEntriesStillShareTheNewBlock()
    {
        var (bars, _, _) = SharedBars();
        byte[] mine = Bwav.MakeStub(Bwav.Build([Tone(30000, 11), Tone(30000, 13)], 44100));

        byte[] rebuilt = TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = mine });

        Assert.Equal(TotkBars.RawBwavOffset(rebuilt, 0), TotkBars.RawBwavOffset(rebuilt, 1));
        Assert.Equal(bars.Length, rebuilt.Length);
    }

    [Fact]
    public void ASharedBlockEveryEntryLeftIsDropped()
    {
        var (bars, _, other) = SharedBars();
        byte[] a = Bwav.MakeStub(Bwav.Build([Tone(30000, 11), Tone(30000, 13)], 44100));
        byte[] b = Bwav.MakeStub(Bwav.Build([Tone(40000, 11), Tone(40000, 13)], 44100));

        byte[] rebuilt = TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [0] = a, [1] = b }, new HashSet<int> { 0, 1 });

        Assert.Equal(30000, Bwav.SampleCount(rebuilt.AsSpan(TotkBars.RawBwavOffset(rebuilt, 0))));
        Assert.Equal(40000, Bwav.SampleCount(rebuilt.AsSpan(TotkBars.RawBwavOffset(rebuilt, 1))));
        Assert.Equal(other, Block(rebuilt, 2)[..other.Length]);
        Assert.Equal(bars.Length + 0xC0, rebuilt.Length);
    }

    [Fact]
    public void AnUnsharedEntryIsReplacedInPlaceEitherWay()
    {
        var (bars, _, _) = SharedBars();
        byte[] mine = Bwav.MakeStub(Bwav.Build([Tone(900, 3)], 48000));

        Assert.Equal(TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [2] = mine }),
            TotkBars.Rebuild(bars, new Dictionary<int, byte[]> { [2] = mine }, new HashSet<int> { 2 }));
    }

    [Fact]
    public void StereoGoesToTheFrontPairOfASurroundLayout()
    {
        short[] l = Tone(1000, 7), r = Tone(1000, 11);
        short[][] six = Bwav.MatchSpeakerLayout([l, r], 6, [0, 1, 2, 3, 4, 5]);

        Assert.Equal(6, six.Length);
        Assert.Equal(l, six[0]);
        Assert.Equal(r, six[1]);
        Assert.All(six[2..], c => Assert.All(c, s => Assert.Equal(0, s)));

        short[][] rearsFirst = Bwav.MatchSpeakerLayout([l, r], 4, [4, 5, 0, 1]);
        Assert.Equal(l, rearsFirst[2]);
        Assert.Equal(r, rearsFirst[3]);
        Assert.All(rearsFirst[..2], c => Assert.All(c, s => Assert.Equal(0, s)));
    }

    [Fact]
    public void MonoGoesToTheCentreOrBothFronts()
    {
        short[] m = Tone(1000, 7);
        short[][] withCentre = Bwav.MatchSpeakerLayout([m], 6, [0, 1, 2, 3, 4, 5]);
        Assert.Equal(m, withCentre[2]);
        Assert.Equal(1, withCentre.Count(c => c.Any(s => s != 0)));

        short[][] noCentre = Bwav.MatchSpeakerLayout([m], 4, [0, 1, 4, 5]);
        Assert.Equal(m, noCentre[0]);
        Assert.Equal(m, noCentre[1]);
        Assert.All(noCentre[2..], c => Assert.All(c, s => Assert.Equal(0, s)));
    }

    [Fact]
    public void LayeredLayoutsAndUnknownLayoutsKeepTheOldRemix()
    {
        short[] l = Tone(1000, 7), r = Tone(1000, 11);
        Assert.Equal(Bwav.MatchChannelCount([l, r], 4), Bwav.MatchSpeakerLayout([l, r], 4, [0, 1, 0, 1]));
        Assert.Equal(Bwav.MatchChannelCount([l, r], 6), Bwav.MatchSpeakerLayout([l, r], 6, null));
        Assert.Equal(Bwav.MatchChannelCount([l, r], 1), Bwav.MatchSpeakerLayout([l, r], 1, [2]));
        Assert.Equal(Bwav.MatchChannelCount([l, r], 6), Bwav.MatchSpeakerLayout([l, r], 6, [2, 3, 4, 5, 6, 7]));
    }

    [Fact]
    public void SpeakerPositionsAreWrittenAsGivenOrDefaultedByLayout()
    {
        short[] t = Tone(1000, 7);
        Assert.Equal(new ushort[] { 2 }, Bwav.ReadPans(Bwav.Build([t], 48000)));
        Assert.Equal(new ushort[] { 0, 1 }, Bwav.ReadPans(Bwav.Build([t, t], 48000)));
        Assert.Equal(new ushort[] { 0, 1, 0, 1 }, Bwav.ReadPans(Bwav.Build([t, t, t, t], 48000)));
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5 }, Bwav.ReadPans(Bwav.Build([t, t, t, t, t, t], 48000)));
        Assert.Equal(new ushort[] { 0, 1, 4, 5 }, Bwav.ReadPans(Bwav.Build([t, t, t, t], 48000, pans: [0, 1, 4, 5])));
        Assert.Throws<ArgumentException>(() => Bwav.Build([t, t], 48000, pans: [0]));
    }

    [Fact]
    public void AnOpusBwavIsLaidOutLikeTheGames()
    {
        short[] left = Tone(30000, 19), right = Tone(30000, 41, 5000);
        byte[] bwav = Bwav.BuildOpus([left, right], 48000, pans: [0, 1]);

        Assert.Equal(BwavCodec.Opus, Bwav.Codec(bwav));
        Assert.False(Bwav.IsPrefetch(bwav));
        Assert.Equal(new ushort[] { 0, 1 }, Bwav.ReadPans(bwav));

        int[] chunkStarts = new int[2], chunkEnds = new int[2];
        for (int c = 0; c < 2; c++)
        {
            int at = Header + c * Channel;
            Assert.Equal(48000u, U32(bwav, at + 4));
            Assert.Equal(30000u, U32(bwav, at + 8));
            Assert.Equal(30000u, U32(bwav, at + 0x0C));
            int offset = (int)U32(bwav, at + 0x34);
            Assert.Equal(offset, (int)U32(bwav, at + 0x30));
            Assert.Equal(0, offset % 0x40);

            Assert.Equal(Convert.FromHexString("0100008018000000000100008" + "0BB0000200000000000000000000000780000000400" + "0080"),
                bwav.AsSpan(offset, 0x24).ToArray());
            int body = (int)U32(bwav, offset + 0x24);
            chunkStarts[c] = offset;
            chunkEnds[c] = offset + 0x28 + body;

            IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
            short[] frame = new short[960];
            int packets = 0;
            for (int p = offset + 0x28; p < chunkEnds[c]; packets++)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(bwav.AsSpan(p));
                uint range = BinaryPrimitives.ReadUInt32BigEndian(bwav.AsSpan(p + 4));
                Assert.Equal(0xF8, bwav[p + 8]);
                Assert.Equal(960, decoder.Decode(bwav.AsSpan(p + 8, size), frame, 960, false));
                Assert.Equal(range, decoder.FinalRange);
                p += 8 + size;
            }
            Assert.Equal((30000 + 120 + 959) / 960, packets);
        }

        uint crc = Crc32.Update(Crc32.Update(0, bwav.AsSpan(chunkStarts[0], chunkEnds[0] - chunkStarts[0])), bwav.AsSpan(chunkStarts[1], chunkEnds[1] - chunkStarts[1]));
        Assert.Equal(crc, U32(bwav, 8));
        Assert.Equal(chunkEnds[1], bwav.Length);

        PcmAudio back = Bwav.Decode(bwav);
        Assert.True(Correlation(left, back.Channels[0]) > 0.99);
        Assert.True(Correlation(right, back.Channels[1]) > 0.99);
    }

    [Fact]
    public void OpusFromAnotherRateIsResampledWithItsLoop()
    {
        short[] tone = Tone(44100, 30);
        byte[] bwav = Bwav.BuildOpus([tone], 44100, 4410, 44100);

        Assert.Equal(48000, Bwav.SampleCount(bwav));
        Assert.Equal((4800, 48000), Bwav.Loop(bwav));
        short[] expected = Bwav.Resample(tone, 44100, 48000);
        Assert.True(Correlation(expected, Bwav.Decode(bwav).Channels[0]) > 0.99);
        Assert.True(Correlation(Tone(48000, 30 * 48000 / 44100.0), expected) > 0.999);
    }

    [Fact]
    public void AnOpusPrefetchClipIsTheDecodedStartAsPcm()
    {
        short[] left = Tone(60000, 19), right = Tone(60000, 41, 5000);
        byte[] full = Bwav.BuildOpus([left, right], 48000);
        byte[] clip = Bwav.MakePrefetch(full, 36864);
        short[][] start = [.. Enumerable.Range(0, 2).Select(c =>
            OpusChannel.Decode(full.AsSpan((int)U32(full, Header + c * Channel + 0x34)), 36864 + Bwav.OpusPrefetchTail, dropPreSkip: false))];

        Assert.True(Bwav.IsPrefetch(clip));
        Assert.Equal(BwavCodec.Opus, Bwav.Codec(clip));
        Assert.Equal(U32(full, 8), U32(clip, 8));
        for (int c = 0; c < 2; c++)
        {
            int at = Header + c * Channel;
            Assert.Equal(60000u, U32(clip, at + 8));
            Assert.Equal(36864u, U32(clip, at + 0x0C));
            Assert.Equal(U32(full, at + 0x30), U32(clip, at + 0x30));
            int offset = (int)U32(clip, at + 0x34);
            Assert.Equal(240u, U32(clip, at + 0x10));
            Assert.Equal((uint)(offset + 36864 * 2), U32(clip, at + 0x14));
            for (int i = 0; i < 36864 + Bwav.OpusPrefetchTail; i++)
                Assert.Equal(start[c][i], BinaryPrimitives.ReadInt16LittleEndian(clip.AsSpan(offset + i * 2)));
        }
        int first = (int)U32(clip, Header + 0x34), second = (int)U32(clip, Header + Channel + 0x34);
        Assert.Equal(0xC0, first);
        Assert.Equal(first + (36864 + 240) * 2 + 0x20, second);
        Assert.Equal(second + (36864 + 240) * 2, clip.Length);

        PcmAudio decodedClip = Bwav.Decode(clip);
        Assert.Equal(36864, decodedClip.SampleCount);
        Assert.Equal(start[1][..36864], decodedClip.Channels[1]);
    }

    [Fact]
    public void AnOpusPrefetchClipNeedsItsLength()
    {
        byte[] full = Bwav.BuildOpus([Tone(5000, 9)], 48000);
        Assert.Throws<ArgumentException>(() => Bwav.MakePrefetch(full));
    }

    [Fact]
    public void AdpcmPrefetchClipsAreUnchanged()
    {
        byte[] full = Bwav.Build([Tone(40000, 17)], 48000);
        Assert.Equal(Bwav.MakePrefetch(full), Bwav.MakePrefetch(full, Bwav.PrefetchSamples));
        Assert.Equal(Bwav.PrefetchSamples, Bwav.SamplesInFile(Bwav.MakePrefetch(full)));
    }
}
