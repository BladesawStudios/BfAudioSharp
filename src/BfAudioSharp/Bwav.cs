using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

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
                case BwavCodec.Opus when IsPrefetch(data):
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
        6 => [0, 1, 2, 3, 4, 5],
        _ => [.. Enumerable.Range(0, count).Select(i => (ushort)(i % 2))],
    };

    public static ushort[] ReadPans(ReadOnlySpan<byte> data)
    {
        int count = ChannelCount(data);
        if (data.Length < HeaderSize + count * ChannelSize) throw new InvalidDataException("The BWAV ends inside its header.");
        ushort[] pans = new ushort[count];
        for (int c = 0; c < count; c++) pans[c] = BinaryPrimitives.ReadUInt16LittleEndian(data[(HeaderSize + c * ChannelSize + 2)..]);
        return pans;
    }

    public static BwavCodec Codec(ReadOnlySpan<byte> data)
    {
        Require(data);
        if (ChannelCount(data) == 0) throw new InvalidDataException("The BWAV has no channels.");
        return (BwavCodec)BinaryPrimitives.ReadUInt16LittleEndian(data[HeaderSize..]);
    }

    public static int SamplesInFile(ReadOnlySpan<byte> data)
    {
        Require(data);
        return ChannelCount(data) == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(HeaderSize + 0x0C)..]);
    }

    public static byte[] MakeStub(ReadOnlySpan<byte> full)
    {
        Require(full);
        int count = ChannelCount(full);
        int headerSize = HeaderSize + ChannelSize * count;
        if (full.Length < headerSize) throw new InvalidDataException("The BWAV ends inside its header.");

        byte[] stub = full[..headerSize].ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x0C), 0);
        for (int c = 0; c < count; c++)
        {
            int at = HeaderSize + c * ChannelSize;
            BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 0x0C), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 0x34), (uint)Align(headerSize, 0x40));
        }
        return stub;
    }

    private static ushort[] PansFor(int count, ushort[]? pans)
    {
        if (pans is null) return Pans(count);
        if (pans.Length != count) throw new ArgumentException($"{pans.Length} speaker positions given for {count} channels", nameof(pans));
        return pans;
    }

    private static (int Samples, int? LoopStart, int? LoopEnd) CheckInput(short[][] channels, int? loopStart, int? loopEnd)
    {
        if (channels.Length == 0 || channels[0].Length == 0) throw new InvalidDataException("No audio samples to encode");
        int samples = channels[0].Length;
        if (channels.Any(c => c.Length != samples)) throw new InvalidDataException("All channels must have the same length");

        if (loopEnd is { } e && e > samples) loopEnd = samples;
        if (loopStart is null || loopEnd is null || loopStart >= loopEnd) (loopStart, loopEnd) = (null, null);
        return (samples, loopStart, loopEnd);
    }

    /// <summary>
    /// Encodes PCM to a whole (non-prefetch) DSP-ADPCM BWAV. The loop is dropped unless it is a non-empty range;
    /// an end past the audio is cut back to its end.
    /// </summary>
    public static byte[] Build(short[][] channels, int sampleRate, int? loopStart = null, int? loopEnd = null, ushort[]? pans = null)
    {
        int samples;
        (samples, loopStart, loopEnd) = CheckInput(channels, loopStart, loopEnd);
        ushort[] channelPans = PansFor(channels.Length, pans);

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
            BinaryPrimitives.WriteUInt16LittleEndian(info[2..], channelPans[c]);
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

    public const int OpusSampleRate = 48000;

    public const int OpusBitrate = 64000;

    private const int OpusFrame = 960;

    public static byte[] BuildOpus(short[][] channels, int sampleRate, int? loopStart = null, int? loopEnd = null, ushort[]? pans = null, int bitrate = OpusBitrate)
    {
        CheckInput(channels, loopStart, loopEnd);
        if (sampleRate != OpusSampleRate)
        {
            channels = [.. channels.Select(c => Resample(c, sampleRate, OpusSampleRate))];
            int? Scale(int? at) => at is { } v ? (int)Math.Round(v * (double)OpusSampleRate / sampleRate) : null;
            (loopStart, loopEnd) = (Scale(loopStart), loopEnd == 0x7FFFFFFF ? loopEnd : Scale(loopEnd));
        }

        int samples;
        (samples, loopStart, loopEnd) = CheckInput(channels, loopStart, loopEnd);
        ushort[] channelPans = PansFor(channels.Length, pans);
        byte[][] encoded = channels.AsParallel().AsOrdered().Select(c => EncodeOpusChannel(c, bitrate)).ToArray();

        int headerSize = HeaderSize + ChannelSize * channels.Length;
        int[] offsets = new int[encoded.Length];
        int position = Align(headerSize, 0x40);
        for (int i = 0; i < encoded.Length; i++)
        {
            offsets[i] = position;
            position = Align(position + encoded[i].Length, 0x40);
        }

        uint crc = 0;
        foreach (byte[] chunk in encoded) crc = Crc32.Update(crc, chunk);

        byte[] output = new byte[offsets[^1] + encoded[^1].Length];
        "BWAV"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), crc);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x0E), (ushort)channels.Length);
        for (int c = 0; c < encoded.Length; c++)
        {
            Span<byte> info = output.AsSpan(HeaderSize + c * ChannelSize, ChannelSize);
            BinaryPrimitives.WriteUInt16LittleEndian(info, (ushort)BwavCodec.Opus);
            BinaryPrimitives.WriteUInt16LittleEndian(info[2..], channelPans[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], OpusSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], (uint)samples);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x0C..], (uint)samples);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x30..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x34..], (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(info[0x38..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(info[0x3C..], loopEnd ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(info[0x40..], loopStart ?? 0);
            encoded[c].CopyTo(output, offsets[c]);
        }
        return output;
    }

    private static byte[] EncodeOpusChannel(short[] pcm, int bitrate)
    {
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(OpusSampleRate, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = bitrate;
        encoder.UseVBR = true;
        encoder.Complexity = 10;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        int skip = encoder.Lookahead;

        short[] padded = new short[(pcm.Length + skip + OpusFrame - 1) / OpusFrame * OpusFrame];
        pcm.CopyTo(padded, 0);

        using MemoryStream body = new();
        byte[] packet = new byte[1275];
        Span<byte> head = stackalloc byte[8];
        for (int at = 0; at < padded.Length; at += OpusFrame)
        {
            int size = encoder.Encode(padded.AsSpan(at, OpusFrame), OpusFrame, packet, packet.Length);
            if (size <= 0) throw new InvalidOperationException($"The Opus encoder failed ({size}).");
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(head[4..], encoder.FinalRange);
            body.Write(head);
            body.Write(packet, 0, size);
        }

        byte[] output = new byte[8 + 0x18 + 8 + body.Length];
        Span<byte> o = output;
        BinaryPrimitives.WriteUInt32LittleEndian(o, 0x80000001);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], 0x18);
        o[9] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x0C..], OpusSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x10..], 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x1C..], (uint)skip);
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x20..], 0x80000004);
        BinaryPrimitives.WriteUInt32LittleEndian(o[0x24..], (uint)body.Length);
        body.ToArray().CopyTo(output, 0x28);
        return output;
    }

    public static short[] Resample(short[] input, int from, int to)
    {
        if (from == to) return input;
        int length = (int)Math.Round(input.Length * (double)to / from);
        IResampler resampler = ResamplerFactory.CreateResampler(1, from, to, 10);
        resampler.SkipZeroes();

        short[] source = new short[input.Length + resampler.InputLatency + 1];
        input.CopyTo(source, 0);
        short[] output = new short[length];
        int read = 0, written = 0;
        while (read < source.Length && written < length)
        {
            int inLength = source.Length - read, outLength = length - written;
            resampler.Process(0, source.AsSpan(read), ref inLength, output.AsSpan(written), ref outLength);
            if (inLength == 0 && outLength == 0) break;
            read += inLength;
            written += outLength;
        }
        return output;
    }

    public const int OpusPrefetchTail = 240;

    /// <summary>
    /// A prefetch clip (the first 0x3800 samples of each channel) made from a whole BWAV. The header, coefficients,
    /// sample count and CRC stay those of the whole sound; only the data is cut, at a frame boundary.
    /// </summary>
    public static byte[] MakePrefetch(ReadOnlySpan<byte> full, int? samples = null)
    {
        Require(full);
        if (samples is <= 0) throw new ArgumentOutOfRangeException(nameof(samples), "A prefetch clip needs at least one sample.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(full[0x0E..]);
        var codec = (BwavCodec)BinaryPrimitives.ReadUInt16LittleEndian(full[HeaderSize..]);
        if (codec == BwavCodec.Opus)
            return MakeOpusPrefetch(full, samples ?? throw new ArgumentException("An Opus prefetch clip needs its length (take it from the clip being replaced).", nameof(samples)));
        if (codec != BwavCodec.DspAdpcm)
            throw new InvalidDataException("Only DSP-ADPCM and Opus BWAVs can be turned into prefetch clips");
        int prefetchSamples = samples ?? PrefetchSamples;
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

            int inFile = (int)Math.Min(full0, (uint)prefetchSamples);
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

    private static byte[] MakeOpusPrefetch(ReadOnlySpan<byte> full, int samples)
    {
        int count = ChannelCount(full);
        int headerSize = HeaderSize + ChannelSize * count;
        if (IsPrefetch(full)) throw new InvalidDataException("The BWAV is already a prefetch clip; make the clip from the whole sound.");

        byte[] header = full[..headerSize].ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 1);

        short[][] blocks = new short[count][];
        int[] offsets = new int[count];
        int position = Align(headerSize, 0x40);
        for (int c = 0; c < count; c++)
        {
            int at = HeaderSize + c * ChannelSize;
            int whole = (int)BinaryPrimitives.ReadUInt32LittleEndian(full[(at + 0x0C)..]);
            int source = (int)BinaryPrimitives.ReadUInt32LittleEndian(full[(at + 0x34)..]);
            if (source < 0 || source >= full.Length) throw new InvalidDataException($"Channel {c} reaches past the end of the file.");
            int stated = Math.Min(whole, samples);
            blocks[c] = OpusChannel.Decode(full[source..], Math.Min(whole, stated + OpusPrefetchTail), dropPreSkip: false);
            int tail = blocks[c].Length - stated;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x0C), (uint)stated);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x10), (uint)tail);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x14), (uint)(position + stated * 2));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 0x34), (uint)position);
            offsets[c] = position;
            position = Align(position + blocks[c].Length * 2, 0x40);
        }

        byte[] output = new byte[offsets[^1] + blocks[^1].Length * 2];
        header.CopyTo(output, 0);
        for (int c = 0; c < count; c++)
            for (int i = 0; i < blocks[c].Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(offsets[c] + i * 2), blocks[c][i]);
        return output;
    }

    public static short[][] MatchSpeakerLayout(short[][] channels, int target, ushort[]? pans)
    {
        int source = channels.Length;
        if (pans is null || pans.Length != target || target <= source || source > 2 || pans.Distinct().Count() != pans.Length)
            return MatchChannelCount(channels, target);

        int left = Array.IndexOf(pans, (ushort)0), right = Array.IndexOf(pans, (ushort)1), centre = Array.IndexOf(pans, (ushort)2);
        int samples = channels[0].Length;
        short[][] output = [.. Enumerable.Range(0, target).Select(_ => new short[samples])];
        if (source == 2 && left >= 0 && right >= 0)
        {
            output[left] = (short[])channels[0].Clone();
            output[right] = (short[])channels[1].Clone();
        }
        else if (source == 1 && centre >= 0)
        {
            output[centre] = (short[])channels[0].Clone();
        }
        else if (source == 1 && left >= 0 && right >= 0)
        {
            output[left] = (short[])channels[0].Clone();
            output[right] = (short[])channels[0].Clone();
        }
        else
        {
            return MatchChannelCount(channels, target);
        }
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
