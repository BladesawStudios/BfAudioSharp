# BfAudioSharp

NintendoWare audio for .NET. Two halves:

- **Cafe / NX streams and archives:** readers for BARS/AMTA, BFWAV, BFSTM, BFSTP, BFSAR, BFWAR and BFGRP, with DSP-ADPCM
  decoding (`Bfsar`, `Bfstm`, `Bfwav`, `Bars`).
- **Tears of the Kingdom sound files:** reading, decoding, building and replacing the BWAV and BARS files of the game.

## Tears of the Kingdom

```csharp
// Decode a BWAV (PCM16 or DSP-ADPCM) and write it as a WAV.
PcmAudio audio = Bwav.Decode(File.ReadAllBytes("Hit.bwav"));
File.WriteAllBytes("Hit.wav", Wav.Write(audio));      // audio.LoopStart / LoopEnd are in samples

// Encode a WAV (16/24/32-bit PCM or float) as a BWAV, with a loop.
PcmAudio wav = Wav.Parse(File.ReadAllBytes("New.wav"));
byte[] bwav = Bwav.Build(wav.Channels, wav.SampleRate, loopStart: 1000, loopEnd: 40000);

// The streaming half of a sound keeps only its first 0x3800 samples inside the BARS.
byte[] clip = Bwav.MakePrefetch(bwav);

// Put it in a BARS, and refresh the loudness numbers its AMTA stores.
byte[] bars = File.ReadAllBytes("Hit.bars");
TotkBars parsed = TotkBars.Parse(bars);
byte[] copy = (byte[])bars.Clone();
AmtaLoudness.Write(copy, parsed.Entries[0].AmtaOffset, AmtaLoudness.Measure(wav.Channels, wav.SampleRate));
byte[] rebuilt = TotkBars.Rebuild(copy, new Dictionary<int, byte[]> { [0] = clip });
```

| | |
| --- | --- |
| `Bwav` | read, decode, `Build`, `MakePrefetch`, `SetLoop`, `MatchChannelCount` (remix) |
| `DspAdpcmEncoder` | a port of the reference DSP-ADPCM encoder (gc-dspadpcm-encode); output matches the Python port byte for byte |
| `Wav` | reads and writes RIFF WAV, including the loop of a `smpl` chunk |
| `TotkBars` | parse the version 5 AMTA blocks of the game; `Rebuild` swaps embedded BWAVs, keeping everything else (unchanged in, identical out) |
| `AmtaLoudness` | the peak and EBU R128 loudness figures an AMTA stores: `Measure`, `LocateBlock`, `Write` |

BWAV (little endian): a 0x10 byte header (`BWAV`, BOM, version 1, CRC32 of the sample data, prefetch flag, channel count), then
0x4C bytes per channel (codec, pan, rate, sample counts, 16 DSP coefficients, data offsets, loop end and start, history), then
each channel's data aligned to 0x40. Decoding matches vgmstream sample for sample (checked on 60 sounds of the game).

Not supported: Opus-coded BWAVs (120 of the game's 21,949 sounds). `Bwav.Decode` throws `NotSupportedException` for those.

## Build

```bash
dotnet build BfAudioSharp.sln -c Release
dotnet test tests/BfAudioSharp.Tests
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
