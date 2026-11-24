using Uno.UI.Hosting;

namespace CaptureRack.Uno;

internal static class Program
{
    [STAThread]
    private static void Main() =>
        UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32()
            .UseMacOS()
            .Build()
            .Run();
}
