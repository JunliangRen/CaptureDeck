using System.Buffers.Binary;

namespace CaptureRack.DirectShow.Windows.Capture;

public static class DirectShowPreviewVideo
{
    private static readonly Guid Rgb24 = new("e436eb7d-524f-11ce-9f53-0020af0ba770");
    private static readonly Guid Rgb32 = new("e436eb7e-524f-11ce-9f53-0020af0ba770");
    private static readonly Guid Argb32 = new("773c9ac0-3274-11d0-b724-00aa006c1a01");

    public static string? Encoding(DirectShowFormat format)
    {
        if (format.Kind != "video" || format.Width is not (> 0 and <= 8192) || format.Height is not (> 0 and <= 8192))
            return null;
        return format.InfraredEncoding ?? (format.FourCc switch
        {
            "YUY2" or "UYVY" or "HDYC" or "YVYU" or "NV12" or "I420" or "IYUV" or "YV12" or
            "v210" or "r210" or "MJPG" or "Y800" or "GREY" => format.FourCc,
            _ when format.Subtype == Rgb24 => "RGB24",
            _ when format.Subtype == Rgb32 || format.Subtype == Argb32 => "RGB32",
            _ => null
        });
    }

    public static bool IsJpeg(string encoding) => encoding is "MJPG" or "MJPG_IR";

    public static (int Width, int Height) DisplaySize(int width, int height)
    {
        double scale = Math.Min(1, Math.Min(1280d / width, 720d / height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static bool TryConvert(DirectShowFormat format, string encoding, ReadOnlySpan<byte> source,
        Span<byte> bgra, int outputWidth, int outputHeight)
    {
        if (format.Width is not (> 0 and <= 8192) || format.Height is not (> 0 and <= 8192) ||
            outputWidth is not (> 0 and <= 1280) || outputHeight is not (> 0 and <= 720))
            return false;
        var width = format.Width.Value;
        var height = format.Height.Value;
        var planar = encoding is "NV12" or "I420" or "IYUV" or "YV12";
        if (planar && ((width | height) & 1) != 0)
            return false;
        var minimumStride = encoding switch
        {
            "RGB24" => (width * 3 + 3) & ~3,
            "RGB32" or "r210" => width * 4,
            "v210" => ((width + 5) / 6) * 16,
            "YUY2" or "UYVY" or "HDYC" or "YVYU" => ((width + 1) / 2) * 4,
            "L16_IR" => width * 2,
            _ => width
        };
        int stride = minimumStride;
        int chromaStride = encoding == "NV12" ? stride : stride / 2;
        int chromaRows = height / 2;
        var available = format.ImageBytes is > 0 && format.ImageBytes.Value <= source.Length
            ? (int)format.ImageBytes.Value : source.Length;
        if (planar)
        {
            // NV12 and I420 derived layouts use the luma pitch and half-resolution chroma.
            int inferred = available / (height + chromaRows);
            if (inferred >= stride && (inferred & 1) == 0 && inferred * (height + chromaRows) == available)
                stride = inferred;
            chromaStride = encoding == "NV12" ? stride : stride / 2;
        }
        else if (available % height == 0 && available / height >= stride)
        {
            stride = available / height;
        }
        int lumaBytes = stride * height;
        int chromaBytes = chromaStride * chromaRows;
        int required = planar ? lumaBytes + chromaBytes * (encoding == "NV12" ? 1 : 2) : lumaBytes;
        if (source.Length < required || bgra.Length < outputWidth * outputHeight * 4)
            return false;

        bool hd = encoding == "HDYC" || height > 576;
        bool bottomUp = (encoding is "RGB24" or "RGB32") && format.BitmapTopDown == false;
        bool preserveFields = (format.InterlaceFlags & 1) == 1 && ((height | outputHeight) & 1) == 0;
        for (int row = 0; row < outputHeight; row++)
        {
            int y = preserveFields ? ((row / 2 * height / outputHeight) * 2 + row % 2) : row * height / outputHeight;
            int rowOffset = (bottomUp ? height - y - 1 : y) * stride;
            var scanline = source.Slice(rowOffset, stride);
            var dest = bgra.Slice(row * outputWidth * 4, outputWidth * 4);
            for (int column = 0; column < outputWidth; column++)
            {
                int x = column * width / outputWidth;
                var pixel = dest.Slice(column * 4, 4);
                pixel[3] = 255;
                if (planar)
                {
                    int uv = lumaBytes + (y / 2) * chromaStride;
                    if (encoding == "NV12")
                        WriteYuv(pixel, scanline[x], source[uv + (x & ~1)], source[uv + (x & ~1) + 1], hd);
                    else
                    {
                        uv += x / 2;
                        int first = source[uv], second = source[uv + chromaBytes];
                        WriteYuv(pixel, scanline[x], encoding == "YV12" ? second : first,
                            encoding == "YV12" ? first : second, hd);
                    }
                }
                else
                    WritePacked(pixel, scanline, x, encoding, hd);
            }
        }
        return true;
    }

    private static void WritePacked(Span<byte> pixel, ReadOnlySpan<byte> row, int x, string encoding, bool hd)
    {
        switch (encoding)
        {
            case "YUY2":
            case "YVYU":
                int yuy2 = (x / 2) * 4;
                WriteYuv(pixel, row[yuy2 + (x % 2) * 2], row[yuy2 + (encoding == "YUY2" ? 1 : 3)],
                    row[yuy2 + (encoding == "YUY2" ? 3 : 1)], hd);
                break;
            case "UYVY":
            case "HDYC":
                int uyvy = (x / 2) * 4;
                WriteYuv(pixel, row[uyvy + 1 + (x % 2) * 2], row[uyvy], row[uyvy + 2], hd);
                break;
            case "v210":
                ReadV210(row, x, out int y, out int u, out int v);
                WriteYuv(pixel, y >> 2, u >> 2, v >> 2, hd);
                break;
            case "r210":
                uint rgb = BinaryPrimitives.ReadUInt32BigEndian(row[(x * 4)..]);
                pixel[0] = (byte)(rgb >> 2);
                pixel[1] = (byte)(rgb >> 12);
                pixel[2] = (byte)(rgb >> 22);
                break;
            case "RGB24":
            case "RGB32":
                row.Slice(x * (encoding == "RGB24" ? 3 : 4), 3).CopyTo(pixel);
                break;
            case "L16_IR":
                pixel[0] = pixel[1] = pixel[2] = row[x * 2 + 1];
                break;
            default:
                pixel[0] = pixel[1] = pixel[2] = row[x];
                break;
        }
    }

    private static void ReadV210(ReadOnlySpan<byte> row, int x, out int y, out int u, out int v)
    {
        var group = row[(x / 6 * 16)..];
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(group);
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(group[4..]);
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(group[8..]);
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(group[12..]);
        int offset = x % 6;
        y = (int)((offset switch { 0 => a >> 10, 1 => b, 2 => b >> 20, 3 => c >> 10, 4 => d, _ => d >> 20 }) & 1023);
        u = (int)((offset < 2 ? a : offset < 4 ? b >> 10 : c >> 20) & 1023);
        v = (int)((offset < 2 ? a >> 20 : offset < 4 ? c : d >> 10) & 1023);
    }

    private static void WriteYuv(Span<byte> pixel, int y, int u, int v, bool hd)
    {
        int luma = 298 * (y - 16);
        int cb = u - 128, cr = v - 128;
        pixel[0] = Clip(luma + (hd ? 541 : 516) * cb);
        pixel[1] = Clip(luma - (hd ? 55 : 100) * cb - (hd ? 136 : 208) * cr);
        pixel[2] = Clip(luma + (hd ? 459 : 409) * cr);
    }

    private static byte Clip(int value) => (byte)Math.Clamp((value + 128) >> 8, 0, 255);
}
