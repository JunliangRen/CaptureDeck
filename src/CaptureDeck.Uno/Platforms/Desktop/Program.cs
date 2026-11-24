using Uno.UI.Hosting;

namespace CaptureDeck.Uno;

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
