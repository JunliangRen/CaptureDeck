using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CaptureCallback(PreviewFeed? preview, CapturePixelFormat pixelFormat,
    CaptureAudioFormat audioFormat, VideoMode mode, MovRecording? recording, bool automaticMode,
    LocalAudioMonitor? localAudioMonitor = null) : IDeckLinkInputCallback
{
    private const uint NoInput = 1u << 31;
    private const uint BufferRead = 1;
    private const long Timescale = 48000;

    private readonly object gate = new();
    private readonly CaptureStats stats = new();
    private readonly FrameTimeline? timeline = preview is null ? null : new(mode);
    private StereoMonitor? audioMonitor = preview is null ? null : new(audioFormat);
    private readonly CaptureAudioFormat defaultOutputAudioFormat = audioFormat;
    private CaptureAudioFormat inputAudioFormat = audioFormat;
    private StereoLevels? lastLevels;
    private FrameTimeline? recordingTimeline;
    private CaptureStats? recordingBaseline;
    private (long Left, long Right) recordingClips;
    private bool recordingModeChanged;
    private bool hasRecordableFrame;
    private long? recordingAudioTime;
    private long? recordingVideoTime;
    private long recordingRegressions;

    // Called once after EnableAudioInput, before StartStreams can deliver packets.
    internal void UseMonitoringAudioFormat(CaptureAudioFormat format)
    {
        inputAudioFormat = format;
        audioMonitor = new StereoMonitor(format);
    }

    internal MovRecording AttachRecording(string path, CaptureAudioFormat? outputAudioFormat = null)
    {
        var output = outputAudioFormat ?? defaultOutputAudioFormat;
        lock (gate)
        {
            EnsureRecordingReady();
            if (!CaptureAudio.SupportsRecordingFormat(inputAudioFormat, output))
                throw new InvalidOperationException("The current preview input does not supply the selected PCM depth.");
        }

        var writer = new MovRecording(path, mode, pixelFormat, output);
        try
        {
            lock (gate)
            {
                EnsureRecordingReady();
                recordingBaseline = stats.Clone();
                recordingModeChanged = false;
                recordingTimeline = new FrameTimeline(mode);
                recordingClips = audioMonitor?.ClipCounts ?? default;
                recordingAudioTime = recordingVideoTime = null;
                recordingRegressions = 0;
                recording = writer;
                return writer;
            }
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private bool CanAttachRecording => stats.Error is null && recording is null && stats.NextMode is null &&
        (!automaticMode || (hasRecordableFrame && !HasDifferentInputMode));

    private bool HasDifferentInputMode => stats.DetectedMode != 0 && stats.DetectedMode != mode.Id;

    private void EnsureRecordingReady()
    {
        if (!CanAttachRecording)
            throw new InvalidOperationException(automaticMode
                ? "Wait for a readable preview frame in the current mode before recording."
                : "The preview is no longer ready to record.");
    }

    internal bool TryBeginModeChange()
    {
        lock (gate)
        {
            if (stats.Error is not null || recording is not null || !HasDifferentInputMode)
                return false;

            stats.NextMode = stats.DetectedMode;
            return true;
        }
    }

    internal CaptureStats DetachRecording()
    {
        lock (gate)
        {
            recording = null;
            var baseline = recordingBaseline!;
            var clips = audioMonitor?.ClipCounts ?? default;
            var result = new CaptureStats
            {
                VideoFrames = stats.VideoFrames - baseline.VideoFrames,
                NoInputFrames = stats.NoInputFrames - baseline.NoInputFrames,
                UnreadableNoInputFrames = stats.UnreadableNoInputFrames - baseline.UnreadableNoInputFrames,
                AudioPackets = stats.AudioPackets - baseline.AudioPackets,
                MissingVideoFrames = recordingTimeline!.MissingFrames,
                TimestampRegressions = recordingRegressions,
                Events = recordingTimeline.Events(),
                AudioLevels = new StereoLevels(0, 0, clips.Left - recordingClips.Left,
                    clips.Right - recordingClips.Right, null, null),
                RecordingModeChanged = recordingModeChanged,
                Error = stats.Error
            };
            recordingBaseline = null;
            recordingTimeline = null;
            recordingModeChanged = false;
            return result;
        }
    }


    internal CaptureStats Snapshot(bool publishMonitor)
    {
        StereoMonitor.AudioWindow? audioWindow;
        CaptureStats snapshot;
        lock (gate)
        {
            audioWindow = publishMonitor ? audioMonitor?.Read() : null;
            snapshot = stats.Clone();
            snapshot.CanStartRecording = CanAttachRecording;
            snapshot.InputAudioFormat = inputAudioFormat;
            snapshot.RecordingModeChanged = recordingModeChanged;
            if (publishMonitor && timeline is not null)
            {
                snapshot.Events = timeline.Events();
                snapshot.MissingVideoFrames = timeline.MissingFrames;
            }
        }

        if (audioWindow is not null)
            lastLevels = audioWindow.Analyze();
        snapshot.AudioLevels = lastLevels;
        return snapshot;
    }

    public int VideoInputFormatChanged(uint events, IntPtr mode, uint flags)
    {
        lock (gate)
        {
            try
            {
                if (mode != IntPtr.Zero)
                {
                    var displayMode = (IDeckLinkDisplayMode)Marshal.GetObjectForIUnknown(mode);
                    try
                    {
                        ObserveDetectedMode(displayMode.GetDisplayMode());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(displayMode);
                    }
                }
            }
            catch (Exception ex)
            {
                stats.Error ??= ex.Message;
            }
        }

        return 0;
    }

    internal void ObserveDetectedMode(uint detectedMode)
    {
        lock (gate)
        {
            if (stats.NextMode is not null || detectedMode == stats.DetectedMode)
                return;

            stats.DetectedMode = detectedMode;
            if (!HasDifferentInputMode)
                return;

            hasRecordableFrame = false;
            if (automaticMode && recording is not null)
                recordingModeChanged = true;
        }
    }

    public int VideoInputFrameArrived(IntPtr video, IntPtr audio)
    {
        lock (gate)
        {
            // Keep the clip in its original mode. The UI finishes it before the worker switches inputs.
            if (stats.Error is not null || recordingModeChanged || (automaticMode && HasDifferentInputMode))
                return 0;

            try
            {
                if (video != IntPtr.Zero)
                {
                    var videoFrame = (IDeckLinkVideoInputFrame)Marshal.GetObjectForIUnknown(video);
                    try
                    {
                        CountVideo(videoFrame);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(videoFrame);
                    }
                }

                if (audio != IntPtr.Zero)
                {
                    var audioPacket = (IDeckLinkAudioInputPacket)Marshal.GetObjectForIUnknown(audio);
                    try
                    {
                        CountAudio(audioPacket);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(audioPacket);
                    }
                }
            }
            catch (Exception ex)
            {
                stats.Error = ex.Message;
            }
        }

        return 0;
    }

    internal void AcceptManagedSamples(IDeckLinkVideoInputFrame? video, IDeckLinkAudioInputPacket audio)
    {
        lock (gate)
        {
            if (video is not null)
                CountVideo(video);
            CountAudio(audio);
            GC.KeepAlive(video);
            GC.KeepAlive(audio);
        }
    }

    // Called under the callback gate. Managed frame fixtures can exercise this path without the driver.
    internal void CountVideo(IDeckLinkVideoInputFrame video)
    {
        var noInput = (video.GetFlags() & NoInput) != 0;
        if (timeline is not null && video.GetStreamTime(out var monitorTime, out _, mode.TimeScale) == 0)
            timeline.Observe(monitorTime, noInput);
        if (noInput)
        {
            hasRecordableFrame = false;
            stats.Signal = VideoSignal.NoInput;
            stats.NoInputFrames++;
            if (preview is not null || recording is not null || stats.NoInputVideoPayload is null)
            {
                try
                {
                    var payload = ReadVideoPayload(video, true, describe: stats.NoInputVideoPayload is null);
                    stats.NoInputVideoPayload ??= payload;
                }
                catch (Exception ex) when ((recording is null || !hasRecordableFrame) &&
                    ex is COMException or InvalidCastException or InvalidOperationException)
                {
                    // Some devices report loss of input without delivering a readable frame.
                    stats.UnreadableNoInputFrames++;
                    stats.NoInputVideoPayload ??= $"unavailable ({ex.Message})";
                }
            }
            return;
        }

        stats.Signal = VideoSignal.Present;
        DeckLinkCapture.Check(video.GetStreamTime(out var time, out _, Timescale), "GetStreamTime");
        if (stats.VideoTime is long previous && time < previous)
            stats.TimestampRegressions++;

        stats.FirstVideoTime ??= time;
        stats.VideoTime = time;
        stats.VideoFrames++;

        if (stats.VideoPayload is null || preview is not null || recording is not null)
        {
            var payload = ReadVideoPayload(video, false, describe: stats.VideoPayload is null);
            stats.VideoPayload ??= payload;
        }
    }

    private string? ReadVideoPayload(IDeckLinkVideoInputFrame video, bool noInput, bool describe)
    {
        var format = video.GetPixelFormat();
        if (format != (uint)pixelFormat)
            throw new InvalidOperationException($"Expected '{DeckLinkCapture.FormatFourCc((uint)pixelFormat)}', " +
                $"received '{DeckLinkCapture.FormatFourCc(format)}'.");

        var buffer = (IDeckLinkVideoBuffer)video;
        DeckLinkCapture.Check(buffer.StartAccess(BufferRead), "Video buffer StartAccess");
        try
        {
            DeckLinkCapture.Check(buffer.GetBytes(out var bytes), "Video buffer GetBytes");
            DeckLinkCapture.Check(buffer.GetSize(out var length), "Video buffer GetSize");
            var rowBytes = video.GetRowBytes();
            var height = video.GetHeight();
            if (bytes == IntPtr.Zero || rowBytes < 4 || height <= 0 || length < (ulong)rowBytes * (ulong)height)
                throw new InvalidOperationException("Invalid video buffer.");

            var width = video.GetWidth();
            hasRecordableFrame = width == mode.Width && height == mode.Height &&
                rowBytes >= MovRecording.StoredRowBytes(mode.Width, pixelFormat);
            if (noInput && recording is not null && !hasRecordableFrame)
                throw new InvalidOperationException("No-input frame has no writable buffer for this mode.");
            if (recording is not null)
            {
                DeckLinkCapture.Check(video.GetStreamTime(out var time, out var duration, recording.VideoTimeScale),
                    "Get recording video stream time");
                recording.SubmitVideo(bytes, width, height, rowBytes, time, duration, stats.Signal);
                recordingTimeline?.Observe(time, noInput);
                ObserveRecordingTimestamp(ref recordingVideoTime, time);
            }
            preview?.Offer(bytes, width, height, rowBytes, length, noInput, pixelFormat, mode.FieldDominance);
            if (!describe)
                return null;

            var firstWord = unchecked((uint)Marshal.ReadInt32(bytes));
            return $"{width}x{height}, {DeckLinkCapture.FormatFourCc(format)}, stride={rowBytes}, " +
                $"{length} bytes, first word=0x{firstWord:X8}";
        }
        finally
        {
            DeckLinkCapture.Check(buffer.EndAccess(BufferRead), "Video buffer EndAccess");
        }
    }

    internal unsafe void CountAudio(IDeckLinkAudioInputPacket audio)
    {
        var sampleFrames = audio.GetSampleFrameCount();
        if (sampleFrames <= 0)
            return;

        DeckLinkCapture.Check(audio.GetPacketTime(out var time, Timescale), "GetPacketTime");
        if (stats.AudioTime is long previous && time < previous)
            stats.TimestampRegressions++;

        stats.FirstAudioTime ??= time;
        stats.AudioTime = time;
        stats.AudioPackets++;
        stats.AudioSampleFrames += sampleFrames;

        DeckLinkCapture.Check(audio.GetBytes(out var bytes), "Audio packet GetBytes");
        if (bytes == IntPtr.Zero)
            throw new InvalidOperationException("Invalid audio buffer.");

        if (recording is not null)
        {
            recording.SubmitAudio(bytes, sampleFrames, time, inputAudioFormat, stats.Signal);
            ObserveRecordingTimestamp(ref recordingAudioTime, time);
        }
        var sampleCount = checked(sampleFrames * (int)CaptureAudio.Channels);
        if (inputAudioFormat == CaptureAudioFormat.Pcm32)
        {
            var span = new ReadOnlySpan<int>((void*)bytes, sampleCount);
            ReadAudioSamples(span);
            localAudioMonitor?.SubmitPcm32(span);
        }
        else
        {
            var span = new ReadOnlySpan<short>((void*)bytes, sampleCount);
            ReadAudioSamples(span);
            localAudioMonitor?.SubmitPcm16(span);
        }

        stats.AudioPayload ??= $"{sampleFrames} stereo sample frames in first packet";
    }

    private void ObserveRecordingTimestamp(ref long? previous, long time)
    {
        if (time < previous)
            recordingRegressions++;
        previous = time;
    }

    private void ReadAudioSamples<T>(ReadOnlySpan<T> samples) where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        for (var i = 0; i < samples.Length; i += 2)
        {
            var left = int.CreateChecked(samples[i]);
            var right = int.CreateChecked(samples[i + 1]);
            stats.AudioPeak = Math.Max(stats.AudioPeak, Math.Max(Math.Abs((long)left), Math.Abs((long)right)));
            audioMonitor?.Add(left, right);
        }
    }
}

public sealed class CaptureStats
{
    public VideoSignal Signal { get; internal set; }
    public bool CanStartRecording { get; internal set; }
    public CaptureAudioFormat? InputAudioFormat { get; internal set; }
    public bool RecordingModeChanged { get; internal set; }
    public long VideoFrames { get; internal set; }
    public long NoInputFrames { get; internal set; }
    public long UnreadableNoInputFrames { get; internal set; }
    public long AudioPackets { get; internal set; }
    public long AudioSampleFrames { get; internal set; }
    public long AudioPeak { get; internal set; }
    public StereoLevels? AudioLevels { get; internal set; }
    public long MissingVideoFrames { get; internal set; }
    public IReadOnlyList<CaptureEvent> Events { get; internal set; } = [];
    internal long? FirstVideoTime;
    internal long? FirstAudioTime;
    public long? VideoTime { get; internal set; }
    public long? AudioTime { get; internal set; }
    public long TimestampRegressions { get; internal set; }
    internal uint DetectedMode;
    internal uint? NextMode;
    public string? VideoPayload { get; internal set; }
    public string? NoInputVideoPayload { get; internal set; }
    public string? AudioPayload { get; internal set; }
    public string? Error { get; internal set; }
    public RecordingResult? Recording { get; internal set; }

    public bool SupportsRecordingAudioFormat(CaptureAudioFormat output) =>
        InputAudioFormat is { } input && CaptureAudio.SupportsRecordingFormat(input, output);

    public bool Verified => Error is null && TimestampRegressions == 0 &&
        VideoPayload is not null && AudioPayload is not null &&
        VideoTime > FirstVideoTime && AudioTime > FirstAudioTime;

    internal CaptureStats Clone() => (CaptureStats)MemberwiseClone();
}
