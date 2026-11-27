using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.Capture;
using CaptureRack.DirectShow.Windows.Interop;
using Xunit;

namespace CaptureRack.UT;

public sealed unsafe class DirectShowFormatTests
{
    [Fact]
    public void DiagnosticsKeepNativeStatusAndDistinguishCaptureFromStopErrors()
    {
        var native = DirectShowDiagnosticFailure.FromException("Run graph", new DirectShowException("Resource error",
            unchecked((int)0x800705AA)));
        Assert.Equal("0x800705AA", native.HResult);
        Assert.Equal("Run graph", native.Stage);

        var stop = DirectShowDiagnosticFailure.FromCaptureError("Stop DirectShow graph failed (0x80004005).");
        Assert.Equal("Stop the DirectShow graph.", stop.Stage);
        Assert.Equal("0x80004005", stop.HResult);
        Assert.Null(DirectShowDiagnosticFailure.FromCaptureError("Capture device disconnected.").HResult);
    }

    [Fact]
    public void CameraControlValidationUsesDriverRangeStepModesAndChoices()
    {
        var focus = new DirectShowControl("Focus", 0, 255, 5, 0, 25,
            DirectShowControlMode.Auto | DirectShowControlMode.Manual, DirectShowControlMode.Auto);
        Assert.Equal(0, DirectShowControls.Validate(focus, new("Focus", 999, DirectShowControlMode.Auto)));
        Assert.Equal(30, DirectShowControls.Validate(focus, new("Focus", 30, DirectShowControlMode.Manual)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectShowControls.Validate(focus, new("Focus", 32, DirectShowControlMode.Manual)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectShowControls.Validate(focus, new("Focus", 260, DirectShowControlMode.Manual)));

        var fieldOfView = new DirectShowControl("FieldOfView", 65, 90, 1, null, 90,
            DirectShowControlMode.Manual, DirectShowControlMode.Manual) { Choices = [65, 78, 90] };
        Assert.Equal(78, DirectShowControls.Validate(fieldOfView, new("FieldOfView", 78, DirectShowControlMode.Manual)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectShowControls.Validate(fieldOfView, new("FieldOfView", 70, DirectShowControlMode.Manual)));
        Assert.Throws<ArgumentException>(() => DirectShowControls.Validate(fieldOfView, new("FieldOfView", 90, DirectShowControlMode.Auto)));
    }

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
    public void LegacyHeaderUsesMatchingExtendedFormatOnlyWhenScanFlagsAgree()
    {
        var legacy = LegacyUyvy(480, 333333);
        var progressive = legacy with { FormatType = new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA"), InterlaceFlags = 0 };
        var interlaced = progressive with { InterlaceFlags = 5 };

        var resolved = CaptureMediaType.ResolveScanMode(legacy, "camera", [progressive, interlaced with { PinId = "1" }]);
        Assert.Equal("progressive", resolved.FieldOrder);
        Assert.Null(resolved.InterlaceFlags);
        Assert.Null(CaptureMediaType.ResolveScanMode(legacy, "camera", [progressive, interlaced]).FieldOrder);
        Assert.Equal("top-first", CaptureMediaType.ResolveScanMode(legacy, "camera",
            [interlaced, interlaced with { InterlaceFlags = 0x85 }]).FieldOrder);
        Assert.Null(CaptureMediaType.ResolveScanMode(legacy, "camera",
            [interlaced, interlaced with { InterlaceFlags = 7 }]).FieldOrder);
        Assert.Null(CaptureMediaType.ResolveScanMode(legacy, "camera", [progressive with { FrameInterval100ns = 400000 }]).FieldOrder);
        Assert.Equal("top-first", CaptureMediaType.ResolveScanMode(interlaced, "camera", [progressive]).FieldOrder);
    }

    [Theory]
    [InlineData(486, 333667L, "bottom-first")]
    [InlineData(576, 400000L, "top-first")]
    [InlineData(1080, 333667L, null)]
    [InlineData(576, 200000L, null)]
    public void BlackmagicLegacyScanFallbackAppliesOnlyToKnownNativeSdModes(int height, long interval, string? expected)
    {
        var format = LegacyUyvy(height, interval) with { Width = height == 1080 ? 1920 : 720 };
        var resolved = CaptureMediaType.ResolveScanMode(format, @"@device:pnp:\\?\decklink#avstream#test", []);

        Assert.Equal(expected, resolved.FieldOrder);
        Assert.Null(resolved.InterlaceFlags);
        Assert.Null(CaptureMediaType.ResolveScanMode(format, "generic capture", []).FieldOrder);
    }

    [Fact]
    public void ObsVirtualCameraProgressiveFallbackKeepsDriverFlagsDistinct()
    {
        const string device = @"@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
        var obs = LegacyUyvy(1080, 166833) with
        {
            Width = 1920, PinId = "Output Pin", FourCc = "NV12", Subtype = new("3231564e-0000-0010-8000-00aa00389b71")
        };
        var resolved = CaptureMediaType.ResolveScanMode(obs, device, []);
        Assert.Null(resolved.InterlaceFlags);
        Assert.Equal("progressive", resolved.FieldOrder);
        Assert.Null(CaptureMediaType.ResolveScanMode(obs, "OBS Virtual Camera", []).FieldOrder);
        Assert.Null(CaptureMediaType.ResolveScanMode(obs with { PinId = "Other" }, device, []).FieldOrder);
        Assert.Equal("top-first", CaptureMediaType.ResolveScanMode(obs with { InterlaceFlags = 0x25 }, device, []).FieldOrder);
    }

    private static DirectShowFormat LegacyUyvy(int height, long interval) => new(0, "0", "Capture", "video",
        new("73646976-0000-0010-8000-00AA00389B71"), new("59565955-0000-0010-8000-00AA00389B71"),
        new("05589F80-C356-11CE-BF01-00AA0055595A"))
    {
        FourCc = "UYVY", Width = 720, Height = height, FrameInterval100ns = interval
    };

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
