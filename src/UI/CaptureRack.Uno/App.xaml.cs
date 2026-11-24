using Microsoft.UI.Xaml;

namespace CaptureRack.Uno;

public sealed partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window
        {
            Title = "CaptureRack QC",
            Content = new MainPage()
        };
        window.Activate();
    }
}
