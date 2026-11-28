using CaptureRack.Audio;
using System.Buffers.Binary;
using System.Text;
using CaptureRack.Core;
using CaptureRack.DeckLink.Monitoring;
using CaptureRack.DeckLink.Recording;

namespace CaptureRack.DeckLink;

// QuickTime sample tables reference the SDK payloads written to mdat. See Apple TN2162.
internal sealed class QuickTimeMovie
{
    private const uint AudioRate = CaptureAudio.SampleRate;
    private const uint MovieRate = 1_000_000;
    private readonly Stream stream;
    private readonly VideoMode mode;
    private readonly CapturePixelFormat format;
    private readonly int videoSampleSize;
    private readonly CaptureAudioFormat audioFormat;
    private readonly uint audioChannels;
    private readonly int audioFrameBytes;
    private readonly long mediaDataOffset;
    private readonly List<long> videoOffsets = [];
    private readonly List<SampleRun> videoDurations = [];
    private readonly List<long> audioOffsets = [];
    private readonly List<ChunkRun> audioChunks = [];
    private long firstVideoTime;
    private readonly FrameTimestampTracker videoTiming;
    private readonly AudioTimestampTracker audioTiming = new();
    private long videoDuration;
    private uint lastVideoDuration;
    private long suspectedMissingVideoFrames;
    private long videoCorrections;
    private long videoInvalidDurations;
    private RecordingTimestampIssue? firstVideoCorrection;
    private long firstAudioTime;

    public int VideoFrames => videoOffsets.Count;
    public long AudioSamples { get; private set; }
    public RecordingTiming Timing => new(suspectedMissingVideoFrames, videoCorrections, videoInvalidDurations, audioTiming.Discontinuities,
        audioTiming.Resets, audioTiming.ApparentGapSamples, audioTiming.ApparentOverlapSamples,
        audioTiming.MaximumOffsetSamples, firstVideoCorrection, audioTiming.FirstIssue);

    public QuickTimeMovie(Stream stream, VideoMode mode, CapturePixelFormat format, int videoSampleSize,
        CaptureAudioFormat audioFormat, uint audioChannels = CaptureAudio.Channels)
    {
        this.stream = stream;
        if (mode.FrameDuration is <= 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mode), "The video mode requires a valid MOV sample duration.");
        this.mode = mode;
        videoTiming = new FrameTimestampTracker(mode.FrameDuration);
        this.format = format;
        this.videoSampleSize = videoSampleSize;
        this.audioFormat = audioFormat;
        this.audioChannels = audioChannels;
        audioFrameBytes = CaptureAudio.BytesPerFrame(audioFormat, audioChannels);
        Atom("ftyp", () =>
        {
            Tag("qt  ");
            U32(0);
            Tag("qt  ");
        });

        mediaDataOffset = stream.Position;
        U32(1);
        Tag("mdat");
        U64(0);
    }

    public void WriteVideo(ReadOnlySpan<byte> data, long time, long duration, VideoSignal signal = VideoSignal.Unknown)
    {
        if (data.Length != videoSampleSize)
            throw new InvalidOperationException("Invalid video sample size.");

        var interval = videoTiming.Observe(time);
        var durationInvalid = duration is <= 0 or > uint.MaxValue;
        if (durationInvalid)
            videoInvalidDurations++;
        if (interval is { } step)
        {
            AddVideoDuration(step.Duration);
            videoDuration += step.Duration;
            suspectedMissingVideoFrames += step.SuspectedMissingFrames;
        }
        else
            firstVideoTime = time;

        if (interval is { Corrected: true } correction)
        {
            videoCorrections++;
            firstVideoCorrection ??= new(videoOffsets.Count + 1L, correction.ExpectedTimestamp, time,
                time - correction.ExpectedTimestamp, null, null, signal.ToString());
        }

        videoOffsets.Add(stream.Position);
        stream.Write(data);
        lastVideoDuration = (uint)(durationInvalid ? mode.FrameDuration : duration);
    }

    public void WriteAudio(ReadOnlySpan<byte> data, long time, int samples, VideoSignal signal = VideoSignal.Unknown)
    {
        if (samples <= 0 || data.Length != checked(samples * audioFrameBytes))
            throw new InvalidOperationException("Invalid stereo PCM sample size.");
        if (AudioSamples + samples > uint.MaxValue)
            throw new InvalidOperationException("The MOV stereo PCM sample table reached its maximum duration.");
        if (audioOffsets.Count == 0)
            firstAudioTime = time;
        audioTiming.Observe(time, samples, signal);

        if (audioChunks.Count == 0 || audioChunks[^1].SamplesPerChunk != (uint)samples)
            audioChunks.Add(new((uint)audioOffsets.Count + 1, (uint)samples));
        audioOffsets.Add(stream.Position);
        stream.Write(data);
        AudioSamples += samples;
    }

    public void Finish()
    {
        if (VideoFrames == 0 && AudioSamples == 0)
            throw new InvalidOperationException("No MOV was saved because the capture did not deliver video or PCM.");

        if (VideoFrames > 0)
            AddVideoDuration(lastVideoDuration);
        var end = stream.Position;
        stream.Position = mediaDataOffset + 8;
        U64(checked((ulong)(end - mediaDataOffset)));
        stream.Position = end;

        var videoStart = VideoFrames > 0 ? (decimal)firstVideoTime / mode.TimeScale : (decimal)firstAudioTime / AudioRate;
        var audioStart = AudioSamples > 0 ? (decimal)firstAudioTime / AudioRate : videoStart;
        var origin = Math.Min(videoStart, audioStart);
        var video = new TrackTiming(checked((uint)mode.TimeScale), checked(videoDuration + lastVideoDuration),
            ToMovieTicks(videoStart - origin));
        var audio = new TrackTiming(AudioRate, AudioSamples, ToMovieTicks(audioStart - origin));

        Atom("moov", () =>
        {
            WriteMovieHeader(Math.Max(video.End, audio.End));
            if (VideoFrames > 0)
                WriteTrack(true, video);
            if (AudioSamples > 0)
                WriteTrack(false, audio);
        });
    }

    private void WriteMovieHeader(ulong duration)
    {
        Atom("mvhd", () =>
        {
            Header(1);
            U64(0);
            U64(0);
            U32(MovieRate);
            U64(duration);
            U32(0x00010000);
            U16(0x0100);
            Zero(10);
            Matrix();
            Zero(24);
            U32(3);
        });
    }

    private void WriteTrack(bool video, TrackTiming timing)
    {
        Atom("trak", () =>
        {
            Atom("tkhd", () =>
            {
                Header(1, 3);
                U64(0);
                U64(0);
                U32(video ? 1u : 2u);
                U32(0);
                U64(timing.End);
                Zero(12);
                U16(video ? (ushort)0 : (ushort)0x0100);
                U16(0);
                Matrix();
                U32(video ? (uint)mode.Width << 16 : 0);
                U32(video ? (uint)mode.Height << 16 : 0);
            });
            if (timing.Offset != 0)
            {
                Atom("edts", () => Atom("elst", () =>
                {
                    Header(1);
                    U32(2);
                    U64(timing.Offset);
                    U64(ulong.MaxValue); // Empty edit (media time -1).
                    U32(0x00010000);
                    U64(timing.MovieDuration);
                    U64(0);
                    U32(0x00010000);
                }));
            }

            Atom("mdia", () =>
            {
                Atom("mdhd", () =>
                {
                    Header(1);
                    U64(0);
                    U64(0);
                    U32(timing.Rate);
                    U64(checked((ulong)timing.Duration));
                    U16(0); // QuickTime language code.
                    U16(0);
                });
                Handler("mhlr", video ? "vide" : "soun");
                Atom("minf", () =>
                {
                    Atom(video ? "vmhd" : "smhd", () =>
                    {
                        Header(0, video ? 1u : 0);
                        Zero(video ? 8 : 4);
                    });
                    Handler("dhlr", "alis");
                    Atom("dinf", () => Atom("dref", () =>
                    {
                        Header(0);
                        U32(1);
                        Atom("alis", () => Header(0, 1)); // Self-contained data reference.
                    }));
                    WriteSampleTables(video);
                });
            });
        });
    }

    private void WriteSampleTables(bool video)
    {
        Atom("stbl", () =>
        {
            Atom("stsd", () =>
            {
                Header(0);
                U32(1);
                if (video)
                    WriteVideoEntry();
                else
                    WriteAudioEntry();
            });
            Atom("stts", () =>
            {
                Header(0);
                if (video)
                {
                    U32((uint)videoDurations.Count);
                    foreach (var run in videoDurations)
                    {
                        U32(run.Count);
                        U32(run.Duration);
                    }
                }
                else
                {
                    U32(1);
                    U32(checked((uint)AudioSamples));
                    U32(1);
                }
            });
            Atom("stsc", () =>
            {
                Header(0);
                if (video)
                {
                    U32(1);
                    Chunk(new(1, 1));
                }
                else
                {
                    U32((uint)audioChunks.Count);
                    foreach (var chunk in audioChunks)
                        Chunk(chunk);
                }
            });
            Atom("stsz", () =>
            {
                Header(0);
                U32(video ? (uint)videoSampleSize : (uint)audioFrameBytes);
                U32(video ? (uint)VideoFrames : checked((uint)AudioSamples));
            });
            Atom("co64", () =>
            {
                var offsets = video ? videoOffsets : audioOffsets;
                Header(0);
                U32((uint)offsets.Count);
                foreach (var offset in offsets)
                    U64((ulong)offset);
            });
        });
    }

    private void WriteVideoEntry()
    {
        var tag = DeckLinkCapture.FormatFourCc((uint)format);
        Atom(tag, () =>
        {
            Zero(6);
            U16(1);
            U16(2);
            U16(0);
            Tag("    ");
            U32(0);
            U32(1024); // Lossless spatial quality.
            U16(checked((ushort)mode.Width));
            U16(checked((ushort)mode.Height));
            U32(72 << 16);
            U32(72 << 16);
            U32(0);
            U16(1);
            U8(4);
            Tag(tag);
            Zero(27);
            U16(24);
            U16(ushort.MaxValue);
            Atom("fiel", () =>
            {
                // Spatially woven fields, tagged by the DeckLink mode's temporal order.
                var detail = mode.FieldDominance switch
                {
                    0x6C6F7772 => (byte)14, // lowr
                    0x75707072 => (byte)9,  // uppr
                    _ => (byte)0
                };
                U8(detail == 0 ? (byte)1 : (byte)2);
                U8(detail);
            });
            Atom("colr", () =>
            {
                Tag("nclc");
                U16(mode.Height <= 486 ? (ushort)6 : mode.Height <= 576 ? (ushort)5 : (ushort)1);
                U16(1);
                U16(mode.Height <= 576 ? (ushort)6 : (ushort)1);
            });
            var ntsc = mode.Width == 720 && mode.Height is 480 or 486;
            var pal = mode.Width == 720 && mode.Height == 576;
            Atom("pasp", () =>
            {
                U32(ntsc ? 10u : pal ? 59u : 1u);
                U32(ntsc ? 11u : pal ? 54u : 1u);
            });
            Atom("clap", () =>
            {
                U32(ntsc ? 704u : pal ? 768u * 54u : (uint)mode.Width);
                U32(pal ? 59u : 1u);
                U32(ntsc ? 480u : (uint)mode.Height);
                U32(1);
                U32(0);
                U32(1);
                U32(0);
                U32(1);
            });
        });
    }

    private void WriteAudioEntry()
    {
        var extended = audioFormat == CaptureAudioFormat.Pcm32 || audioChannels > 2;
        Atom(extended ? "lpcm" : "sowt", () =>
        {
            Zero(6);
            U16(1);
            U16(extended ? (ushort)2 : (ushort)0);
            U16(0);
            U32(0);
            if (extended)
            {
                // QuickTime SoundDescriptionV2 and interleaved signed, packed little-endian PCM.
                U16(3);
                U16(16);
                U16(0xFFFE);
                U16(0);
                U32(0x00010000);
                U32(72);
                U64(BitConverter.DoubleToUInt64Bits(AudioRate));
                U32(audioChannels);
                U32(0x7F000000);
                U32((uint)audioFormat);
                U32(0xC); // Signed integer | packed.
                U32((uint)audioFrameBytes);
                U32(1);
                return;
            }

            U16((ushort)audioChannels);
            U16(16);
            U16(0);
            U16(0);
            U32(AudioRate << 16);
        });
    }

    private void Handler(string kind, string subtype)
    {
        Atom("hdlr", () =>
        {
            Header(0);
            Tag(kind);
            Tag(subtype);
            Zero(13);
        });
    }

    private void AddVideoDuration(uint duration)
    {
        if (videoDurations.Count > 0 && videoDurations[^1].Duration == duration)
        {
            var last = videoDurations[^1];
            videoDurations[^1] = last with { Count = last.Count + 1 };
        }
        else
        {
            videoDurations.Add(new(1, duration));
        }
    }

    private void Chunk(ChunkRun chunk)
    {
        U32(chunk.FirstChunk);
        U32(chunk.SamplesPerChunk);
        U32(1); // Sample description index.
    }

    private void Atom(string tag, Action write)
    {
        var start = stream.Position;
        U32(0);
        Tag(tag);
        write();
        var end = stream.Position;
        stream.Position = start;
        U32(checked((uint)(end - start)));
        stream.Position = end;
    }

    private void Matrix()
    {
        U32(0x00010000);
        Zero(12);
        U32(0x00010000);
        Zero(12);
        U32(0x40000000);
    }

    private void Header(byte version, uint flags = 0) => U32(((uint)version << 24) | flags);
    private void Tag(string tag) => stream.Write(Encoding.ASCII.GetBytes(tag));
    private void U8(byte value) => stream.WriteByte(value);

    private void Zero(int count)
    {
        Span<byte> zero = stackalloc byte[count];
        zero.Clear();
        stream.Write(zero);
    }

    private void U16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private void U32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private void U64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static ulong ToMovieTicks(decimal seconds) => checked((ulong)Math.Round(seconds * MovieRate));

    private readonly record struct SampleRun(uint Count, uint Duration);
    private readonly record struct ChunkRun(uint FirstChunk, uint SamplesPerChunk);
    private readonly record struct TrackTiming(uint Rate, long Duration, ulong Offset)
    {
        public ulong MovieDuration => ToMovieTicks((decimal)Duration / Rate);
        public ulong End => checked(MovieDuration + Offset);
    }
}
