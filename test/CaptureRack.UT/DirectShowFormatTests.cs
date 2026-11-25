using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.Capture;
using CaptureRack.DirectShow.Windows.Interop;
using Xunit;

namespace CaptureRack.UT;

public sealed unsafe class DirectShowFormatTests
{
    [Theory]
    [InlineData("00000032-0002-0010-8000-00aa00389b71", "L8_IR")]
    [InlineData("00000051-0002-0010-8000-00aa00389b71", "L16_IR")]
    [InlineData("47504a4d-0002-0010-8000-00aa00389b71", "MJPG_IR")]
    [InlineData("00000032-0000-0010-8000-00aa00389b71", null)]
    public void InfraredUsesTheDeclaredSubtype(string subtype, string? expected)
    {
        var format = new DirectShowFormat(0, "1", "Capture", "video", Guid.Empty, Guid.Parse(subtype), Guid.Empty);
        Assert.Equal(expected, format.InfraredEncoding);
        Assert.Equal(expected is not null, format.IsInfrared);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "top-first")]
    public void VideoHeaderKeepsInterlaceUnknownWhenMissing(bool extended, string? fieldOrder)
    {
        Span<byte> header = stackalloc byte[112];
        header.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(header[40..], 333667);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 5);
        var bitmap = header[(extended ? 72 : 48)..];
        BinaryPrimitives.WriteInt32LittleEndian(bitmap[4..], 720);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap[8..], -486);
        "v210"u8.CopyTo(bitmap[16..]);
        fixed (byte* data = header)
        {
            var type = new DirectShowMediaType
            {
                Major = new("73646976-0000-0010-8000-00AA00389B71"),
                FormatType = extended ? new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA") : new("05589F80-C356-11CE-BF01-00AA0055595A"),
                Format = data,
                FormatSize = (uint)(extended ? 112 : 88)
            };
            var format = CaptureMediaType.Describe(type, 1, "0", "Output0");
            Assert.Equal(("v210", 720, 486, 333667L, fieldOrder),
                (format.FourCc, format.Width, format.Height, format.FrameInterval100ns, format.FieldOrder));
            type.FormatSize = 20;
            Assert.Null(CaptureMediaType.Describe(type, 1, "0", "Output0").Width);
        }
    }

    [Fact]
    public void PcmHeaderKeepsContainerBitsAndValidBitsDistinct()
    {
        Span<byte> header = stackalloc byte[40];
        header.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0xfffe);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 8);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], 22);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], 24);
        new Guid("00000001-0000-0010-8000-00AA00389B71").TryWriteBytes(header[24..]);
        fixed (byte* data = header)
        {
            var type = new DirectShowMediaType
            {
                Major = new("73647561-0000-0010-8000-00AA00389B71"),
                FormatType = new("05589F81-C356-11CE-BF01-00AA0055595A"),
                Format = data,
                FormatSize = (uint)header.Length
            };
            var format = CaptureMediaType.Describe(type, 0, "1", "Output1");
            Assert.True(format.IsPcm);
            Assert.Equal((8, 48000, 32, 24, 32), (format.Channels, format.SampleRate, format.BitsPerSample,
                format.ValidBitsPerSample, format.BlockAlign));
            type.FormatSize = 18;
            Assert.Null(CaptureMediaType.Describe(type, 0, "1", "Output1").ValidBitsPerSample);
        }
    }
}
