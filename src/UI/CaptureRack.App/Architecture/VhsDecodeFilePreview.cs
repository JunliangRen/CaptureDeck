using System.Buffers;
using CaptureRack.DeckLink;
using VHSDecode.Preview;

namespace CaptureRack.App;

internal static class VhsDecodeFilePreview
{
    // PreviewFeed copies the submitted frame before returning.
    internal static Task<int> DecodeAsync(
        PreviewFileOptions options,
        PreviewFeed feed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return Task.Run(() => PreviewFileDecoder.Decode(options, frame =>
        {
            int byteCount = checked(frame.Width * frame.Height * 4);
            byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Span<byte> bgra = pixels.AsSpan(0, byteCount);
                frame.CopyToBgra(bgra);
                feed.SubmitDecodedFrame(bgra, frame.Width, frame.Height,
                    frame.TopFieldFirst, frame.FramesPerSecond, frame.Width, frame.Height,
                    frame.DisplayAspectRatio, signalScanMode: frame.TopFieldFirst ? PreviewScanMode.TopFirst : PreviewScanMode.BottomFirst);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }, cancellationToken), cancellationToken);
    }
}
