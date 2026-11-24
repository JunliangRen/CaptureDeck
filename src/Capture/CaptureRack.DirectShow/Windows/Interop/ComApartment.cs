using System.Runtime.InteropServices;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Interop;

internal sealed partial class ComApartment : IDisposable
{
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint mode);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    public ComApartment() => Check(CoInitializeEx(0, 0), "Initialize multithreaded COM apartment");
    public void Dispose() => CoUninitialize();
}
