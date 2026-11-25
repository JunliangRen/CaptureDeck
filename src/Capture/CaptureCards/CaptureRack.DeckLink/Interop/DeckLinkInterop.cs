using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

[ComImport, Guid("46C1332E-6FD9-472A-8591-FE59C22192E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkEncoderInput;

// Windows COM ABI from Blackmagic Desktop Video SDK 16.0, Win/include.
// Prefixes of interfaces are declared in IDL order. Unused pointer parameters
// are represented as IntPtr so the slots retain their native signatures.
[ComImport, Guid("50FB36CD-3063-4B73-BDBB-958087F2D8BA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkIterator
{
    [PreserveSig] int Next(out IDeckLink? device);
}

[ComImport, Guid("C418FBDD-0587-48ED-8FE5-640F0A14AF91"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLink
{
    [PreserveSig] int GetModelName([MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.BStr)] out string name);
}

[ComImport, Guid("7BEA3C68-730D-4322-AF34-8A7152B532A4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkApiInformation
{
    [PreserveSig] int GetFlag(uint id, out int value);
    [PreserveSig] int GetInt(uint id, out long value);
}

[ComImport, Guid("6A515F8A-FBCE-4853-B0F7-2A09DB1ECA0B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkInput
{
    [PreserveSig] int DoesSupportVideoMode(uint connection, uint requestedMode, uint pixelFormat,
        uint conversionMode, uint flags, out uint actualMode, out int supported);
    [PreserveSig] int GetDisplayMode(uint mode, out IDeckLinkDisplayMode displayMode);
    [PreserveSig] int GetDisplayModeIterator(out IDeckLinkDisplayModeIterator iterator);
    [PreserveSig] int SetScreenPreviewCallback(IntPtr callback);
    [PreserveSig] int EnableVideoInput(uint mode, uint pixelFormat, uint flags);
    [PreserveSig] int EnableVideoInputWithAllocatorProvider(uint mode, uint pixelFormat, uint flags, IntPtr provider);
    [PreserveSig] int DisableVideoInput();
    [PreserveSig] int GetAvailableVideoFrameCount(out uint count);
    [PreserveSig] int EnableAudioInput(uint sampleRate, uint sampleType, uint channels);
    [PreserveSig] int DisableAudioInput();
    [PreserveSig] int GetAvailableAudioSampleFrameCount(out uint count);
    [PreserveSig] int StartStreams();
    [PreserveSig] int StopStreams();
    [PreserveSig] int PauseStreams();
    [PreserveSig] int FlushStreams();
    [PreserveSig] int SetCallback(IDeckLinkInputCallback? callback);
}

[ComImport, Guid("9C88499F-F601-4021-B80B-032E4EB41C35"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkDisplayModeIterator
{
    [PreserveSig] int Next(out IDeckLinkDisplayMode? mode);
}

[ComImport, Guid("3EB2C1AB-0A3D-4523-A3AD-F40D7FB14E78"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkDisplayMode
{
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] uint GetDisplayMode();
    [PreserveSig] int GetWidth();
    [PreserveSig] int GetHeight();
    [PreserveSig] int GetFrameRate(out long duration, out long scale);
    [PreserveSig] uint GetFieldDominance();
}

[ComImport, Guid("3A94F075-C37D-4BA8-BCC0-1D778C8F881B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkInputCallback
{
    [PreserveSig] int VideoInputFormatChanged(uint events, IntPtr mode, uint flags);
    [PreserveSig] int VideoInputFrameArrived(IntPtr video, IntPtr audio);
}

// Flatten IDeckLinkVideoFrame's inherited slots before GetStreamTime.
[ComImport, Guid("C9ADD3D2-BE52-488D-AB2D-7FDEF7AF0C95"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkVideoInputFrame
{
    [PreserveSig] int GetWidth();
    [PreserveSig] int GetHeight();
    [PreserveSig] int GetRowBytes();
    [PreserveSig] uint GetPixelFormat();
    [PreserveSig] uint GetFlags();
    [PreserveSig] int GetTimecode(uint format, out IntPtr timecode);
    [PreserveSig] int GetAncillaryData(out IntPtr data);
    [PreserveSig] int GetStreamTime(out long time, out long duration, long scale);
}

[ComImport, Guid("732E723C-D1A4-4E29-9E8E-4A88797A0004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkScanAncillary
{
    [PreserveSig] int GetBufferForVerticalBlankingLine(uint line, out IntPtr buffer);
    [PreserveSig] uint GetPixelFormat();
}

[ComImport, Guid("8A72D630-8070-4D05-8A93-E60C40EE088A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkScanAncillaryPackets
{
    [PreserveSig] int GetPacketIterator(out IDeckLinkScanAncillaryIterator? iterator);
}

[ComImport, Guid("10F1AA88-54BE-42F7-B9F8-EC2F5F099551"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkScanAncillaryIterator
{
    [PreserveSig] int Next(out IDeckLinkScanAncillaryPacket? packet);
}

[ComImport, Guid("F5C0D498-5CD3-4C77-9773-8EFA20BB334B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkScanAncillaryPacket
{
    [PreserveSig] int GetBytes(uint format, out IntPtr data, out uint size);
    [PreserveSig] byte GetDid();
    [PreserveSig] byte GetSdid();
    [PreserveSig] uint GetLineNumber();
    [PreserveSig] byte GetDataStreamIndex();
    [PreserveSig] uint GetDataSpace();
}

[ComImport, Guid("81F03D70-DE13-4B17-873A-C8AC9689C682"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkVideoBuffer
{
    [PreserveSig] int GetBytes(out IntPtr bytes);
    [PreserveSig] int GetSize(out ulong size);
    [PreserveSig] int StartAccess(uint flags);
    [PreserveSig] int EndAccess(uint flags);
}

[ComImport, Guid("E43D5870-2894-11DE-8C30-0800200C9A66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkAudioInputPacket
{
    [PreserveSig] int GetSampleFrameCount();
    [PreserveSig] int GetBytes(out IntPtr bytes);
    [PreserveSig] int GetPacketTime(out long time, long scale);
}

[ComImport, Guid("5A68FFD4-1C12-4EDE-A6D2-45451D385FC1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkConfiguration
{
    [PreserveSig] int SetFlag(uint id, int value);
    [PreserveSig] int GetFlag(uint id, out int value);
    [PreserveSig] int SetInt(uint id, long value);
    [PreserveSig] int GetInt(uint id, out long value);
    [PreserveSig] int SetFloat(uint id, double value);
    [PreserveSig] int GetFloat(uint id, out double value);
}

[ComImport, Guid("2A04A635-ED42-41EF-9342-0E11F8CF6B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkStatus
{
    [PreserveSig] int GetFlag(uint id, out int value);
    [PreserveSig] int GetInt(uint id, out long value);
}

[ComImport, Guid("F47551D7-AD22-47AF-BCFD-6BE88AA879D9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkProfileAttributes
{
    [PreserveSig] int GetFlag(uint id, out int value);
    [PreserveSig] int GetInt(uint id, out long value);
}
