using System.Buffers.Binary;

namespace BfAudioSharp;

public enum BwavCodec : ushort { Pcm16 = 0, DspAdpcm = 1, Opus = 2 }

/// <summary>Audio as 16-bit PCM, one array per channel, with an optional loop (in samples; the end is exclusive).</summary>
public sealed class PcmAudio
{
    public int SampleRate;
    public short[][] Channels = [];
    public int? LoopStart;
    public int? LoopEnd;

    public int SampleCount => Channels.Length == 0 ? 0 : Channels[0].Length;
}

/// <summary>
/// BWAV, the sound file Tears of the Kingdom keeps under <c>Sound/Resource</c> and inside BARS archives. Little endian:
/// a 0x10 byte header, then 0x4C bytes per channel, then each channel's samples aligned to 0x40.
/// <code>
/// 0x00 "BWAV"   0x04 BOM FFFE (as FE FF)   0x06 version (1)   0x08 CRC32 of all sample data   0x0C prefetch flag   0x0E channels
/// per channel (0x4C): +00 codec  +02 pan  +04 sample rate  +08 samples (whole sound)  +0C samples (in this file)
///   +10 16 DSP coefficients  +30 data offset (whole)  +34 data offset (in this file)  +38 flag (1)  +3C loop end (-1 none)
///   +40 loop start  +44 predictor/scale  +46 history 1  +48 history 2
/// </code>
/// A prefetch BWAV (flag 1) holds only the first 0x3800 samples of a sound that streams from <c>Sound/Resource/Stream</c>.
/// </summary>
public static class Bwav
{
    public const int PrefetchSamples = 0x3800;
    private const int HeaderSize = 0x10;
    private const int ChannelSize = 0x4C;

    public static bool IsBwav(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("BWAV"u8);

    private static void Require(ReadOnlySpan<byte> data)
    {
        if (!IsBwav(data)) throw new InvalidDataException("Not a BWAV file");
    }

    public static int ChannelCount(ReadOnlySpan<byte> data)
    {
        Require(data);
        return BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
    }

    /// <summary>Samples in the whole sound (the first channel's), whether or not this file holds all of them.</summary>
    public static int SampleCount(ReadOnlySpan<byte> data)
    {
        Require(data);
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(HeaderSize + 8)..]);
    }

    public static bool IsPrefetch(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]) != 0;

    /// <summary>True when a BWAV has no playable samples: no channels, or none in this file (an empty prefetch or placeholder clip).</summary>
    public static bool IsDummy(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x1C || !IsBwav(data)) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]) == 0) return true;
        return BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]) == 0;
    }

    /// <summary>The loop written in the first channel, or null for none. Start is inclusive and end exclusive.</summary>
    public static (int Start, int End)? Loop(ReadOnlySpan<byte> data)
    {
        Require(data);
        int end = BinaryPrimitives.ReadInt32LittleEndian(data[(HeaderSize + 0x3C)..]);
        int start = BinaryPrimitives.ReadInt32LittleEndian(data[(HeaderSize + 0x40)..]);
        return end == -1 ? null : (start, end);
    }

    // ---- decoding ----

    /// <summary>
    /// Decodes every channel of a PCM16, DSP-ADPCM or Opus BWAV (as stored, a prefetch clip giving the samples it holds).
    /// </summary>
    public static PcmAudio Decode(ReadOnlySpan<byte> data)
    {
        Require(data);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
        if (data.Length < HeaderSize + count * ChannelSize) throw new InvalidDataException("The BWAV ends inside its header.");
        if (count == 0) throw new InvalidDataException("The BWAV has no channels.");

        PcmAudio audio = new() { Channels = new short[count][] };
        for (int c = 0; c < count; c++)
        {
            ReadOnlySpan<byte> info = data[(HeaderSize + c * ChannelSize)..];
            var codec = (BwavCodec)BinaryPrimitives.ReadUInt16LittleEndian(info);
            int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            int samples = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x0C..]);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x34..]);
            if (c == 0)
            {
                audio.SampleRate = rate;
                if (Loop(data) is { } loop)
                {
                    audio.LoopStart = loop.Start;
                    audio.LoopEnd = loop.End;
                }
            }

            if (c == 0 && audio.LoopEnd is { } loopEnd && (loopEnd > samples || audio.LoopStart >= loopEnd))
            {
                // The loop belongs to the whole sound: a prefetch clip that stops before it has no loop of its own.
                audio.LoopStart = audio.LoopEnd = null;
            }

            short[] pcm = new short[samples];
            switch (codec)
            {
                case BwavCodec.DspAdpcm:
                {
                    short[] coefs = new short[16];
                    for (int i = 0; i < 16; i++) coefs[i] = BinaryPrimitives.ReadInt16LittleEndian(info[(0x10 + i * 2)..]);
                    short yn1 = BinaryPrimitives.ReadInt16LittleEndian(info[0x46..]), yn2 = BinaryPrimitives.ReadInt16LittleEndian(info[0x48..]);
                    long length = Dsp.ByteLength(samples);
                    if (offset < 0 || offset + length > data.Length) throw new InvalidDataException($"Channel {c} reaches past the end of the file.");
                    Dsp.Decode(data.Slice(offset, (int)length), coefs, yn1, yn2, pcm);
                    break;
                }
                case BwavCodec.Pcm16:
                {
                    long length = samples * 2L;
                    if (offset < 0 || offset + length > data.Length) throw new InvalidDataException($"Channel {c} reaches past the end of the file.");
                    for (int i = 0; i < samples; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(offset + i * 2)..]);
                    break;
                }
                case BwavCodec.Opus:
                {
                    if (offset < 0 || offset >= data.Length) throw new InvalidDataException($"Channel {c} reaches past the end of the file.");
                    pcm = OpusChannel.Decode(data[offset..], samples);
                    break;
                }
                default:
                    throw new NotSupportedException($"BWAV codec {codec} is not supported.");
            }
            audio.Channels[c] = pcm;
        }
        return audio;
    }

    // ---- building ----

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static ushort[] Pans(int count) => count switch
    {
        1 => [2],
        2 => [0, 1],
        _ => [.. Enumerable.Repeat((ushort)2, count)],
    };

    /// <summary>
    /// Encodes PCM to a whole (non-prefetch) DSP-ADPCM BWAV. The loop is dropped unless it is a non-empty range;
    /// an end past the audio is cut back to its end.
    /// </summary>
    public static byte[] Build(short[][] channels, int sampleRate, int? loopStart = null, int? loopEnd = null)
    {
        if (channels.Length == 0 || channels[0].Length == 0) throw new InvalidDataException("No audio samples to encode");
        int samples = channels[0].Length;
        if (channels.Any(c => c.Length != samples)) throw new InvalidDataException("All channels must have the same length");

        if (loopEnd is { } e && e > samples) loopEnd = samples;
        if (loopStart is null || loopEnd is null || loopStart >= loopEnd) (loopStart, loopEnd) = (null, null);

        var encoded = channels.AsParallel().AsOrdered().Select(c => DspAdpcmEncoder.Encode(c)).ToArray();

        int headerSize = HeaderSize + ChannelSize * channels.Length;
        int dataStart = Align(headerSize, 0x40);
        int[] offsets = new int[encoded.Length];
        int position = dataStart;
        for (int i = 0; i < encoded.Length; i++)
        {
            offsets[i] = position;
            position = Align(position + encoded[i].Data.Length, 0x40);
        }

        uint crc = 0;
        foreach (var (_, adpcm) in encoded) crc = Crc32.Update(crc, adpcm);

        ushort[] pans = Pans(channels.Length);
        int total = offsets[^1] + encoded[^1].Data.Length;
        byte[] output = new byte[total];
        "BWAV"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), crc);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x0C), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x0E), (ushort)channels.Length);

        for (int c = 0; c < encoded.Length; c++)
        {
            Span<byte> info = output.AsSpan(HeaderSize + c * ChannelSize, ChannelSize);
            var (coefs, adpcm) = encoded[c];
            BinaryPrimitives.WriteUInt16LittleEndian(info, (ushort)BwavCodec.DspAdpcm);
            BinaryPrimitives.WriteUInt16LittleEndian(info[2..], pans[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], (uint)sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], (uint)samples);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x0C..], (uint)samples);
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteInt16LittleEndian(info[(0x10 + i * 2)..], coefs[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x30..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x34..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x38..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(info[0x3C..], loopEnd ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(info[0x40..], loopStart ?? 0);
            BinaryPrimitives.WriteUInt16LittleEndian(info[0x44..], adpcm[0]);
            adpcm.CopyTo(output.AsSpan(offsets[c]));
        }
        return output;
    }

    /// <summary>
    /// A prefetch clip (the first 0x3800 samples of each channel) made from a whole BWAV. The header, coefficients,
    /// sample count and CRC stay those of the whole sound; only the data is cut, at a frame boundary.
    /// </summary>
    public static byte[] MakePrefetch(ReadOnlySpan<byte> full)
    {
        Require(full);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(full[0x0E..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(full[HeaderSize..]) != (ushort)BwavCodec.DspAdpcm)
            throw new InvalidDataException("Only DSP-ADPCM BWAVs can be turned into prefetch clips");
        int headerSize = HeaderSize + ChannelSize * count;

        byte[] header = full[..headerSize].ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 1);

        List<byte[]> chunks = [];
        int[] offsets = new int[count];
        int position = Align(headerSize, 0x40);
        for (int c = 0; c < count; c++)
        {
            int at = HeaderSize + c * ChannelSize;
            uint full0 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at + 8));
            int source = (int)BinaryPrimitives.ReadUInt32LittleEndian(full[(at + 0x34)..]);

            int inFile = (int)Math.Min(full0, PrefetchSamples);
            int frames = (inFile + Dsp.FrameSamples - 1) / Dsp.FrameSamples;
            int bytes = frames * Dsp.FrameBytes;

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x0C), (uint)inFile);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x34), (uint)position);
            chunks.Add(full.Slice(source, Math.Min(bytes, full.Length - source)).ToArray());
            offsets[c] = position;
            position = Align(position + bytes, 0x40);
        }

        byte[] output = new byte[offsets[^1] + chunks[^1].Length];
        header.CopyTo(output, 0);
        for (int c = 0; c < count; c++) chunks[c].CopyTo(output, offsets[c]);
        return output;
    }

    /// <summary>
    /// A copy with the loop set on every channel. A null <paramref name="loopStart"/> clears it; an end past the sample
    /// count is cut back to it, and an empty range clears the loop.
    /// </summary>
    public static byte[] SetLoop(ReadOnlySpan<byte> bwav, int? loopStart, int? loopEnd)
    {
        Require(bwav);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bwav[0x0E..]);
        byte[] output = bwav.ToArray();
        for (int c = 0; c < count; c++)
        {
            int at = HeaderSize + c * ChannelSize;
            uint whole = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at + 8));
            int? start = loopStart, end = loopEnd;
            if (end is { } e && e > whole) end = (int)whole;
            int writeStart = 0, writeEnd = -1;
            if (start is not null && end is not null && start < end) (writeStart, writeEnd) = (start.Value, end.Value);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + 0x3C), writeEnd);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + 0x40), writeStart);
        }
        return output;
    }

    /// <summary>
    /// Remixes to <paramref name="target"/> channels. Fewer channels average source channel i into output i % target (stereo to
    /// mono averages left and right); more channels repeat the sources in turn (mono to stereo duplicates).
    /// </summary>
    public static short[][] MatchChannelCount(short[][] channels, int target)
    {
        int source = channels.Length;
        if (target < 1 || source == target) return channels;
        if (target > source) return [.. Enumerable.Range(0, target).Select(c => (short[])channels[c % source].Clone())];

        int samples = channels[0].Length;
        short[][] output = new short[target][];
        for (int c = 0; c < target; c++)
        {
            short[][] group = [.. channels.Where((_, i) => i % target == c)];
            short[] mixed = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double sum = 0;
                foreach (short[] ch in group) sum += ch[i];
                int v = (int)Math.Round(sum / group.Length, MidpointRounding.ToEven);
                mixed[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
            output[c] = mixed;
        }
        return output;
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>zlib's crc32: continue from <paramref name="crc"/> (0 to start).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc ^= 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
