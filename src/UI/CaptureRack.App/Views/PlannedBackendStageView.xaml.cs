using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CaptureRack.App.Views;

public sealed partial class PlannedBackendStageView : UserControl
{
    private CaptureSourceDescriptor? activeDescriptor;

    public event EventHandler? SwitchToDeckLinkRequested;

    public PlannedBackendStageView() => InitializeComponent();

    public void Bind(CaptureSourceDescriptor descriptor, Func<string, string> localize)
    {
        activeDescriptor = descriptor;
        BackendIcon.Glyph = descriptor.IconGlyph;
        BackendTitle.Text = localize(descriptor.DisplayNameKey);
        BackendSubtitle.Text = localize(descriptor.SubtitleKey);
        StatusBadgeText.Text = localize("SourceStatusPlanned");
        CapabilitiesHeading.Text = localize("PlannedCapabilitiesHeading");
        CliHeading.Text = localize("PlannedCliHeading");
        PlannedNoticeText.Text = localize("PlannedBackendStageNotice");
        SwitchToDeckLinkButton.Content = localize("SwitchBackToDeckLink");
        ToolTipService.SetToolTip(CopyCliButton, localize("PlannedCliHeading"));
        AutomationProperties.SetName(CopyCliButton, localize("PlannedCliHeading"));

        var caps = descriptor.Capabilities;
        List<string> labels = [];
        if (caps.SupportsVideoPreview) labels.Add(localize("CapVideoPreview"));
        if (caps.SupportsVideoScopes) labels.Add(localize("ScopesTab"));
        if (caps.SupportsAudioMeters) labels.Add(localize("StereoMeter"));
        if (caps.SupportsAudioSpectrum) labels.Add(localize("SpectrumTab"));
        if (caps.SupportsEvents) labels.Add(localize("EventsTab"));
        if (caps.SupportsTransportControl) labels.Add(localize("CapTransportControl"));
        if (caps.SupportsStandaloneAudioOverlay) labels.Add(localize("CapAudioOverlay"));
        CapabilitiesList.ItemsSource = labels.ToArray();

        if (!string.IsNullOrWhiteSpace(descriptor.CliCommandHint))
        {
            CliCommandText.Text = descriptor.CliCommandHint;
            CliHintCard.Visibility = Visibility.Visible;
        }
        else
        {
            CliHintCard.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyCliButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeDescriptor?.CliCommandHint is not { Length: > 0 } command)
            return;

        var package = new DataPackage();
        package.SetText(command);
        Clipboard.SetContent(package);
        Controls.StudioFeedback.FlashCopied(CopyCliIcon, "\uE8C8", themeRoot: this);
    }

    private void SwitchToDeckLinkButton_Click(object sender, RoutedEventArgs e) =>
        SwitchToDeckLinkRequested?.Invoke(this, EventArgs.Empty);
}
