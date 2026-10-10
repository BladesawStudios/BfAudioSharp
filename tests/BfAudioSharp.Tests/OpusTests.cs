using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace BfAudioSharp.Tests;

public class OpusTests
{
    private const int Frame = 960;

    /// <summary>One mono Opus channel in Nintendo's wrapper: header chunk, data chunk, then size + range + packet per frame.</summary>
    private static byte[] Channel(short[] pcm)
    {
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 64000;
        int skip = encoder.Lookahead; // what the decoder drops so the sound starts on time
        using MemoryStream body = new();
        short[] padded = new short[(pcm.Length + skip + Frame - 1) / Frame * Frame];
        pcm.CopyTo(padded, 0);
        byte[] packet = new byte[1275];
        for (int at = 0; at < padded.Length; at += Frame)
        {
            int size = encoder.Encode(padded.AsSpan(at, Frame), Frame, packet, packet.Length);
            Span<byte> head = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)size);
            body.Write(head);
            body.Write(packet, 0, size);
        }

        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        w.Write(0x80000001u); w.Write(0x18u);
        w.Write((byte)0); w.Write((byte)1); w.Write((ushort)0); w.Write(48000u);
        w.Write(0x20u); w.Write(0u); w.Write(0u); w.Write((uint)skip);
        w.Write(0x80000004u); w.Write((uint)body.Length);
        w.Write(body.ToArray());
        return stream.ToArray();
    }

    private static byte[] OpusBwav(short[][] channels, int samples)
    {
        byte[][] data = [.. channels.Select(Channel)];
        int dataStart = (0x10 + 0x4C * channels.Length + 0x3F) & ~0x3F;
        int total = dataStart;
        int[] offsets = new int[data.Length];
        for (int i = 0; i < data.Length; i++) { offsets[i] = total; total = (total + data[i].Length + 0x3F) & ~0x3F; }

        byte[] file = new byte[total];
        "BWAV"u8.CopyTo(file);
        file[4] = 0xFF; file[5] = 0xFE; file[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0E), (ushort)channels.Length);
        for (int c = 0; c < data.Length; c++)
        {
            Span<byte> info = file.AsSpan(0x10 + c * 0x4C);
            BinaryPrimitives.WriteUInt16LittleEndian(info, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], 48000);
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], (uint)samples);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x0C..], (uint)samples);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x30..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x34..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x38..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(info[0x3C..], -1);
            data[c].CopyTo(file, offsets[c]);
        }
        return file;
    }

    [Fact]
    public void OpusChannelsDecodeToTheirTones()
    {
        const int samples = 9600;
        short[] a = [.. Enumerable.Range(0, samples).Select(i => (short)(9000 * Math.Sin(i / 12.0)))];
        short[] b = [.. Enumerable.Range(0, samples).Select(i => (short)(6000 * Math.Sin(i / 31.0)))];

        PcmAudio audio = Bwav.Decode(OpusBwav([a, b], samples));

        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Channels.Length);
        Assert.Equal(samples, audio.SampleCount);
        Assert.True(Correlation(a, audio.Channels[0]) > 0.99);
        Assert.True(Correlation(b, audio.Channels[1]) > 0.99);
        Assert.True(Correlation(a, audio.Channels[1]) < 0.5); // not mixed up
    }

    private static double Correlation(short[] x, short[] y)
    {
        double xy = 0, xx = 0, yy = 0;
        for (int i = 0; i < x.Length; i++) { xy += (double)x[i] * y[i]; xx += (double)x[i] * x[i]; yy += (double)y[i] * y[i]; }
        return xy / Math.Sqrt(xx * yy);
    }

    [Fact]
    public void ATruncatedOpusChannelFailsClearly()
    {
        byte[] file = OpusBwav([new short[1920]], 1920);
        Assert.Throws<InvalidDataException>(() => Bwav.Decode(file.AsSpan(0, 0x10 + 0x4C + 4)));
    }
}
