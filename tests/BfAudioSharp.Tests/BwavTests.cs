using System.Buffers.Binary;

namespace BfAudioSharp.Tests;

public class BwavTests
{
    private static short[] Tone(int samples, double period, double amplitude = 9000)
        => [.. Enumerable.Range(0, samples).Select(i => (short)(amplitude * Math.Sin(i / period)))];

    private static double Psnr(short[] a, short[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += (a[i] - b[i]) * (double)(a[i] - b[i]);
        return sum == 0 ? 99 : 10 * Math.Log10(32767.0 * 32767.0 / (sum / a.Length));
    }

    [Fact]
    public void AnEncodedToneDecodesBackClose()
    {
        short[] left = Tone(5000, 17), right = Tone(5000, 29, 4000);

        byte[] bwav = Bwav.Build([left, right], 32000);
        PcmAudio back = Bwav.Decode(bwav);

        Assert.Equal(32000, back.SampleRate);
        Assert.Equal(2, back.Channels.Length);
        Assert.Equal(5000, back.SampleCount);
        Assert.True(Psnr(left, back.Channels[0]) > 40);
        Assert.True(Psnr(right, back.Channels[1]) > 40);
        Assert.Null(back.LoopStart);
    }

    [Fact]
    public void TheHeaderIsLaidOutTheWayTheGameReadsIt()
    {
        byte[] bwav = Bwav.Build([Tone(100, 5), Tone(100, 7)], 48000, 10, 90);

        Assert.True(Bwav.IsBwav(bwav));
        Assert.Equal(2, Bwav.ChannelCount(bwav));
        Assert.Equal(100, Bwav.SampleCount(bwav));
        Assert.False(Bwav.IsPrefetch(bwav));
        Assert.Equal((10, 90), Bwav.Loop(bwav));
        // Pans for stereo, and every channel's data on a 0x40 boundary with nothing after the last frame.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bwav.AsSpan(0x10 + 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bwav.AsSpan(0x10 + 0x4C + 2)));
        int firstOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bwav.AsSpan(0x10 + 0x34));
        int secondOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bwav.AsSpan(0x10 + 0x4C + 0x34));
        Assert.Equal(0, firstOffset % 0x40);
        Assert.Equal(0, secondOffset % 0x40);
        Assert.Equal(secondOffset + 8 * 8, bwav.Length);
    }

    [Fact]
    public void ALoopOutsideTheAudioIsCutBackOrDropped()
    {
        Assert.Equal((10, 100), Bwav.Loop(Bwav.Build([Tone(100, 5)], 48000, 10, 5000)));
        Assert.Null(Bwav.Loop(Bwav.Build([Tone(100, 5)], 48000, 60, 40)));
        Assert.Null(Bwav.Loop(Bwav.Build([Tone(100, 5)], 48000)));
    }

    [Fact]
    public void SettingTheLoopPatchesEveryChannel()
    {
        byte[] bwav = Bwav.Build([Tone(200, 5), Tone(200, 6)], 48000);

        byte[] looped = Bwav.SetLoop(bwav, 20, 0x7FFFFFFF);

        Assert.Equal((20, 200), Bwav.Loop(looped));
        Assert.Equal(200, BinaryPrimitives.ReadInt32LittleEndian(looped.AsSpan(0x10 + 0x4C + 0x3C)));
        Assert.Null(Bwav.Loop(Bwav.SetLoop(looped, null, null)));
        Assert.Equal(bwav, Bwav.SetLoop(looped, null, null));
    }

    [Fact]
    public void APrefetchClipKeepsTheHeaderAndTheFirstSamples()
    {
        short[] tone = Tone(40000, 23);
        byte[] full = Bwav.Build([tone], 32000);

        byte[] prefetch = Bwav.MakePrefetch(full);

        Assert.True(Bwav.IsPrefetch(prefetch));
        Assert.Equal(40000, Bwav.SampleCount(prefetch));
        PcmAudio clip = Bwav.Decode(prefetch);
        Assert.Equal(Bwav.PrefetchSamples, clip.SampleCount);
        Assert.Equal(Bwav.Decode(full).Channels[0][..Bwav.PrefetchSamples], clip.Channels[0]);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(prefetch.AsSpan(8)));
    }

    [Fact]
    public void ALoopPastAPrefetchClipIsNotReported()
    {
        byte[] prefetch = Bwav.MakePrefetch(Bwav.Build([Tone(40000, 23)], 32000, 100, 39000));

        Assert.Null(Bwav.Decode(prefetch).LoopStart);
    }

    [Fact]
    public void EmptyBwavsAreDummies()
    {
        Assert.False(Bwav.IsDummy(Bwav.Build([Tone(50, 3)], 32000)));
        byte[] empty = Bwav.Build([Tone(50, 3)], 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(0x1C), 0);
        Assert.True(Bwav.IsDummy(empty));
        Assert.False(Bwav.IsDummy("not a bwav at all, but long enough......"u8));
    }

    [Fact]
    public void SilenceEncodesAndDecodesToSilence()
    {
        PcmAudio back = Bwav.Decode(Bwav.Build([new short[300]], 16000));

        Assert.All(back.Channels[0], s => Assert.Equal(0, s));
    }

    [Fact]
    public void ChannelsAreRemixedByAveragingOrRepeating()
    {
        short[] a = [100, 200, -100], b = [300, 0, -300];

        short[][] mono = Bwav.MatchChannelCount([a, b], 1);
        short[][] stereo = Bwav.MatchChannelCount([a], 2);

        Assert.Equal([200, 100, -200], mono[0]);
        Assert.Equal(a, stereo[0]);
        Assert.Equal(a, stereo[1]);
        Assert.NotSame(stereo[0], stereo[1]);
        Assert.Same(a, Bwav.MatchChannelCount([a], 1)[0]);
    }

    [Fact]
    public void AnOpusChannelWithoutItsHeaderIsRejected()
    {
        byte[] bwav = Bwav.Build([Tone(100, 3)], 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bwav.AsSpan(0x10), (ushort)BwavCodec.Opus);

        Assert.Throws<InvalidDataException>(() => Bwav.Decode(bwav));
    }

    [Fact]
    public void TheCrcCoversTheSampleDataOfEveryChannel()
    {
        byte[] bwav = Bwav.Build([Tone(500, 9), Tone(500, 11)], 32000);

        uint crc = 0;
        for (int c = 0; c < 2; c++)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bwav.AsSpan(0x10 + c * 0x4C + 0x34));
            crc = Crc32.Update(crc, bwav.AsSpan(offset, 36 * 8));
        }

        Assert.Equal(crc, BinaryPrimitives.ReadUInt32LittleEndian(bwav.AsSpan(8)));
        Assert.Equal(0xCBF43926u, Crc32.Update(0, "123456789"u8));
    }
}
