using Microsoft.UI.Xaml;

namespace CaptureDeck.Uno;

public sealed partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window
        {
            Title = "CaptureDeck QC",
            Content = new MainPage()
        };
        window.Activate();
    }
}
