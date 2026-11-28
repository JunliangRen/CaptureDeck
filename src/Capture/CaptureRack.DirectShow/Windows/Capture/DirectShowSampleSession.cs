using System.Buffers;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.QualityControl;
using CaptureRack.Recording;

namespace CaptureRack.DirectShow.Windows.Capture;

// A negotiated graph supplies borrowed samples. The attached writer sees each sample
// before the independently bounded preview queue chooses its latest frame.
public sealed class DirectShowSampleSession
{
    private readonly Channel<RawFrame> frames;
    private DirectShowFormat? videoFormat, audioFormat;
    private readonly Lock recordingGate = new();
    private DirectShowRecording? recording;
    private bool ended;
    private long received, unreadable, skippedBeforeDecode;
    public long SkippedBeforeDecode => Interlocked.Read(ref skippedBeforeDecode);

    public DirectShowSampleSession()
    {
        frames = Channel.CreateBounded<RawFrame>(
            new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            frame => { Interlocked.Increment(ref skippedBeforeDecode); frame.Dispose(); });
    }

    public ChannelReader<RawFrame> Frames => frames.Reader;

    public DirectShowRecording StartRecording(string path, (string?, string?) tools = default, string? device = null,
        Action<NativeContainerAttempt>? progress = null, RecordingEncoding? recordingEncoding = null)
    {
        lock (recordingGate)
        {
            if (ended || videoFormat is null || recording is not null)
                throw new InvalidOperationException("The DirectShow recording cannot start in the current preview state.");
            recording = DirectShowRecording.Start(path, videoFormat, audioFormat, tools, device, progress, recordingEncoding);
            return recording;
        }
    }

    public void FinishRecording(DirectShowRecording target)
    {
        lock (recordingGate)
        {
            if (recording == target)
                recording = null;
            target.Complete();
        }
    }

    public void End(Exception? error)
    {
        lock (recordingGate)
        {
            ended = true;
            frames.Writer.TryComplete();
            recording?.Complete(error);
            recording = null;
        }
    }

    public string? Encoding { get; private set; }

    public long Unreadable => Interlocked.Read(ref unreadable);
    public DirectShowFormat? NegotiatedVideo => Volatile.Read(ref videoFormat);
    public DirectShowFormat? NegotiatedAudio => Volatile.Read(ref audioFormat);

    public long Received => Interlocked.Read(ref received);
    public void SetFormats(DirectShowFormat video, DirectShowFormat? audio)
    {
        Encoding = DirectShowPreviewVideo.Encoding(video);
        if (Encoding is null && !video.IsMpeg2Video)
            throw new NotSupportedException(video.Description);
        lock (recordingGate)
        {
            Volatile.Write(ref audioFormat, audio);
            Volatile.Write(ref videoFormat, video);
        }
    }

    public bool ReceiveVideo(CaptureSample sample)
    {
        lock (recordingGate)
            recording?.Accept(true, sample);
        Interlocked.Increment(ref received);
        if (Encoding is null)
            return true;
        if (sample.Bytes.IsEmpty || sample.Bytes.Length > 256 * 1024 * 1024)
        {
            Interlocked.Increment(ref unreadable);
            return true;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(sample.Bytes.Length);
        sample.Bytes.CopyTo(buffer);
        var frame = new RawFrame(buffer, sample.Bytes.Length);
        if (!frames.Writer.TryWrite(frame))
            frame.Dispose();
        return true;
    }

    public void ReceiveAudio(CaptureSample sample)
    {
        lock (recordingGate)
            recording?.Accept(false, sample);
    }

    public sealed class RawFrame : IDisposable
    {
        internal RawFrame(byte[] buffer, int length)
        {
            this.buffer = buffer;
            Length = length;
        }
        private byte[]? buffer;
        public byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(RawFrame));
        public int Length { get; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref buffer, null) is { } owned) ArrayPool<byte>.Shared.Return(owned);
        }
    }
}
