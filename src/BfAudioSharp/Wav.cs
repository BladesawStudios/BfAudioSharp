using System.Buffers.Binary;

namespace BfAudioSharp;

/// <summary>RIFF WAV reading and writing for <see cref="PcmAudio"/>.</summary>
public static class Wav
{
    public static bool IsWav(ReadOnlySpan<byte> data)
        => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8);

    /// <summary>
    /// Reads 16-bit, 24-bit and 32-bit PCM and 32-bit float WAVs (also in the extensible form) to 16-bit PCM. The loop
    /// comes from the first loop of a <c>smpl</c> chunk, if there is one.
    /// </summary>
    public static PcmAudio Parse(ReadOnlySpan<byte> wav)
    {
        if (!IsWav(wav)) throw new InvalidDataException("Not a RIFF WAV file");

        ReadOnlySpan<byte> fmt = default, data = default;
        bool hasFmt = false, hasData = false;
        int? loopStart = null, loopEnd = null;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            ReadOnlySpan<byte> id = wav.Slice(pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(pos + 4)..]);
            int available = (int)Math.Min(size, (uint)(wav.Length - pos - 8));
            ReadOnlySpan<byte> body = wav.Slice(pos + 8, available);
            if (id.SequenceEqual("fmt "u8)) { fmt = body; hasFmt = true; }
            else if (id.SequenceEqual("data"u8)) { data = body; hasData = true; }
            else if (id.SequenceEqual("smpl"u8) && body.Length >= 0x34 && BinaryPrimitives.ReadUInt32LittleEndian(body[0x1C..]) >= 1)
            {
                loopStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[0x2C..]);
                loopEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[0x30..]);
            }
            pos += 8 + (int)Math.Min(size, int.MaxValue - 16) + (int)(size & 1);
        }

        if (!hasFmt || !hasData || fmt.Length < 16) throw new InvalidDataException("WAV file is missing fmt or data chunk");

        int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channelCount = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (format == 0xFFFE && fmt.Length >= 0x1A) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[0x18..]);
        if (channelCount < 1) throw new InvalidDataException("WAV has no channels");

        int bytesPerSample = bits / 8;
        if (!(format == 1 && bits is 16 or 24 or 32) && !(format == 3 && bits == 32))
            throw new InvalidDataException($"Unsupported WAV format (format={format}, bits={bits}); use 16-bit PCM, 24-bit PCM, or 32-bit float");

        int total = data.Length / bytesPerSample;
        int frames = total / channelCount;
        short[][] channels = new short[channelCount][];
        for (int c = 0; c < channelCount; c++) channels[c] = new short[frames];

        for (int i = 0; i < frames * channelCount; i++)
        {
            ReadOnlySpan<byte> s = data[(i * bytesPerSample)..];
            short value;
            if (format == 3)
            {
                float f = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s));
                value = (short)Math.Clamp((int)Math.Round(f * 32767.0, MidpointRounding.ToEven), -32768, 32767);
            }
            else if (bits == 16) value = BinaryPrimitives.ReadInt16LittleEndian(s);
            else if (bits == 24) value = (short)(((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) >> 16);
            else value = (short)(BinaryPrimitives.ReadInt32LittleEndian(s) >> 16);
            channels[i % channelCount][i / channelCount] = value;
        }

        return new PcmAudio { SampleRate = sampleRate, Channels = channels, LoopStart = loopStart, LoopEnd = loopEnd };
    }

    /// <summary>A 16-bit PCM WAV, the channels interleaved.</summary>
    public static byte[] Write(PcmAudio audio)
    {
        int channels = audio.Channels.Length, frames = audio.SampleCount;
        int dataLength = frames * channels * 2;
        byte[] output = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)(36 + dataLength));
        "WAVEfmt "u8.CopyTo(output.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24), (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28), (uint)(audio.SampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), 16);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(40), (uint)dataLength);

        int at = 44;
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++, at += 2)
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(at), audio.Channels[c][i]);
        return output;
    }
}
