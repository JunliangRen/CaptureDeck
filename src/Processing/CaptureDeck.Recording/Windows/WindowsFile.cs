namespace CaptureDeck.Recording.Windows;

internal static partial class WindowsFile
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MoveFileEx(string source, string destination, uint flags);

    public static void MoveWriteThrough(string source, string destination)
    {
        const uint WriteThrough = 8;
        if (MoveFileEx(source, destination, WriteThrough) == 0)
            throw new IOException($"Could not finalize {destination}.",
                new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
    }
}
