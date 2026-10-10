using System.Buffers.Binary;

namespace BfAudioSharp.Tests;

public class WavTests
{
    [Fact]
    public void AWavRoundTrips()
    {
        PcmAudio audio = new() { SampleRate = 22050, Channels = [[1, -2, 3, 32767], [-32768, 5, 6, 7]] };

        PcmAudio back = Wav.Parse(Wav.Write(audio));

        Assert.Equal(22050, back.SampleRate);
        Assert.Equal(audio.Channels[0], back.Channels[0]);
        Assert.Equal(audio.Channels[1], back.Channels[1]);
    }

    private static byte[] Riff(int format, int bits, int channels, byte[] data, byte[]? extra = null)
    {
        List<byte> fmt = [];
        fmt.AddRange(BitConverter.GetBytes((ushort)format));
        fmt.AddRange(BitConverter.GetBytes((ushort)channels));
        fmt.AddRange(BitConverter.GetBytes(8000));
        fmt.AddRange(BitConverter.GetBytes(8000 * channels * bits / 8));
        fmt.AddRange(BitConverter.GetBytes((ushort)(channels * bits / 8)));
        fmt.AddRange(BitConverter.GetBytes((ushort)bits));

        List<byte> body = [.. "WAVE"u8.ToArray()];
        body.AddRange("fmt "u8.ToArray());
        body.AddRange(BitConverter.GetBytes(fmt.Count));
        body.AddRange(fmt);
        if (extra is not null) body.AddRange(extra);
        body.AddRange("data"u8.ToArray());
        body.AddRange(BitConverter.GetBytes(data.Length));
        body.AddRange(data);
        List<byte> file = [.. "RIFF"u8.ToArray()];
        file.AddRange(BitConverter.GetBytes(body.Count));
        file.AddRange(body);
        return [.. file];
    }

    [Fact]
    public void Wider24And32BitAndFloatSamplesAreReducedTo16Bit()
    {
        PcmAudio pcm24 = Wav.Parse(Riff(1, 24, 1, [0x00, 0x34, 0x12, 0xFF, 0xFF, 0xFF]));
        PcmAudio pcm32 = Wav.Parse(Riff(1, 32, 1, BitConverter.GetBytes(0x12345678)));
        PcmAudio floats = Wav.Parse(Riff(3, 32, 1, [.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(-2f)]));

        Assert.Equal([0x1234, -1], pcm24.Channels[0]);
        Assert.Equal([0x1234], pcm32.Channels[0]);
        Assert.Equal([16384, -32768], floats.Channels[0]);
    }

    [Fact]
    public void ALoopInTheSmplChunkIsRead()
    {
        byte[] smpl = new byte[8 + 0x44];
        "smpl"u8.CopyTo(smpl);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(4), 0x44);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(8 + 0x1C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(8 + 0x2C), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(8 + 0x30), 900);

        PcmAudio audio = Wav.Parse(Riff(1, 16, 1, new byte[2000], smpl));

        Assert.Equal(100, audio.LoopStart);
        Assert.Equal(900, audio.LoopEnd);
    }

    [Fact]
    public void ThingsThatAreNotUsableWavsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => Wav.Parse(new byte[50]));
        Assert.Throws<InvalidDataException>(() => Wav.Parse(Riff(1, 8, 1, new byte[10])));
    }
}
