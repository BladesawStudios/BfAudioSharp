using System.Buffers.Binary;

namespace BfAudioSharp.Tests;

public class LoudnessTests
{
    [Fact]
    public void SilenceIsAtTheFloor()
    {
        LoudnessStats stats = AmtaLoudness.Measure([new short[48000]], 48000);

        Assert.Equal(0.0, stats.Peak);
        Assert.Equal(AmtaLoudness.FloorDb, stats.MomentaryMax);
        Assert.Equal(AmtaLoudness.FloorDb, stats.Integrated);
        Assert.Equal(0.0, stats.LoudnessRange);
    }

    [Fact]
    public void ALoudSineHasTheExpectedPeakAndLoudness()
    {
        short[] sine = [.. Enumerable.Range(0, 48000 * 4).Select(i => (short)(16384 * Math.Sin(2 * Math.PI * 1000 * i / 48000)))];

        LoudnessStats stats = AmtaLoudness.Measure([sine], 48000);

        Assert.Equal(0.5, stats.Peak, 3);
        // A 1 kHz sine at half scale is about -9 LUFS by BS.1770.
        Assert.InRange(stats.Integrated, -10.5, -8.0);
        Assert.InRange(stats.MomentaryMax, -10.5, -8.0);
    }

    [Fact]
    public void LoudnessCanBeWrittenIntoAnAmtaAndFoundAgain()
    {
        byte[] amta = new byte[0x100];
        "AMTA"u8.CopyTo(amta);
        amta[7] = 5;
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0x24), 0x80);
        // A flags word, then peak, rms, momentary, integrated and range, at 0x40.
        BinaryPrimitives.WriteUInt32LittleEndian(amta.AsSpan(0x40), 0x4F);
        BinaryPrimitives.WriteSingleLittleEndian(amta.AsSpan(0x44), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(amta.AsSpan(0x4C), -20f);
        BinaryPrimitives.WriteSingleLittleEndian(amta.AsSpan(0x50), -22f);

        Assert.Equal(0x40, AmtaLoudness.LocateBlock(amta, 0));
        Assert.True(AmtaLoudness.Write(amta, 0, new LoudnessStats(0.5, 0.25, -12, -14, 3)));
        Assert.Equal(-14f, BinaryPrimitives.ReadSingleLittleEndian(amta.AsSpan(0x50)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(amta.AsSpan(0x44)));
        Assert.False(AmtaLoudness.Write(new byte[0x100], 0, new LoudnessStats(0, 0, 0, 0, 0)));
    }
}
