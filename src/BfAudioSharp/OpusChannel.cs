using System.Buffers.Binary;
using Concentus;

namespace BfAudioSharp;

/// <summary>
/// The Opus stream inside one channel of an Opus BWAV: Nintendo's wrapper (a 0x80000001 header chunk, a 0x80000004 data chunk)
/// around mono Opus packets, each preceded by its size and final range as big-endian 32-bit numbers.
/// </summary>
internal static class OpusChannel
{
    private const uint HeaderMagic = 0x80000001, DataMagic = 0x80000004;

    /// <summary>Decodes the channel's packets to at most <paramref name="samples"/> samples, after the stream's pre-skip.</summary>
    public static short[] Decode(ReadOnlySpan<byte> data, int samples)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderMagic)
            throw new InvalidDataException("The Opus channel does not start with a header chunk.");

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (headerSize < 0x18 || data.Length < 8 + headerSize) throw new InvalidDataException("The Opus header chunk is truncated.");
        ReadOnlySpan<byte> header = data[8..];
        int channels = header[1];
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int skip = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[0x14..]);
        if (channels != 1) throw new NotSupportedException($"Opus channel with {channels} channels (only mono is used in a BWAV).");

        int position = 8 + headerSize;
        if (data.Length < position + 8 || BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) != DataMagic)
            throw new InvalidDataException("The Opus channel has no data chunk.");
        int dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
        position += 8;
        int end = Math.Min(data.Length, position + dataSize);

        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(rate, 1);
        short[] result = new short[samples];
        short[] frame = new short[5760];
        int produced = 0, toSkip = skip;
        byte[] packet = new byte[1275 + 8];

        while (position + 8 <= end && produced < samples)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            position += 8;
            if (size <= 0 || position + size > end) break;
            data.Slice(position, size).CopyTo(packet);
            position += size;

            int decoded = decoder.Decode(packet.AsSpan(0, size), frame, frame.Length, false);
            int from = Math.Min(toSkip, decoded);
            toSkip -= from;
            int take = Math.Min(decoded - from, samples - produced);
            Array.Copy(frame, from, result, produced, take);
            produced += take;
        }
        return result;
    }
}
