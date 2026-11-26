using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace CaptureRack.SignalCapture;

internal sealed class DomesdaySession(string path)
{
    private static readonly ConcurrentDictionary<string, string> locked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> cleanupErrors = [];
    private Exception? first;

    public static void CheckPath(string path)
    {
        if (locked.TryGetValue(path, out string? reason))
            throw new IOException($"DdD device locked for this process: {reason}");
    }

    public void Fail(Exception error) => first ??= error;

    public void Cleanup(string operation, Action action, bool lockOnFailure = true)
    {
        try { action(); }
        catch (Exception ex)
        {
            Fail(ex);
            string message = $"{operation}: {ex.Message}";
            cleanupErrors.Add(message);
            if (lockOnFailure) locked.TryAdd(path, message);
        }
    }

    public void Rollback(bool legacy, bool testMode, Action<byte, ushort> command, Action<byte, byte> register)
    {
        if (legacy)
        {
            if (testMode) Cleanup("Reset legacy test mode", () => command(0xb6, 0));
            return;
        }

        Cleanup("Stop collection", () => command(0xb5, 0));
        if (testMode) Cleanup("Reset test mode", () => register(0x10, 0));
        Cleanup("Restore decimation", () => register(0x12, 1));
    }

    public void ThrowIfFailed()
    {
        if (first is null) return;
        if (cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(first).Throw();
        string suffix = locked.ContainsKey(path) ? " DdD device locked for this process." : "";
        throw new IOException($"{first.Message} [Cleanup: {string.Join("; ", cleanupErrors)}]{suffix}", first);
    }
}
