using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class PlannedBackendInspectorView : UserControl
{
    public PlannedBackendInspectorView() => InitializeComponent();

    public void Bind(CaptureSourceDescriptor descriptor, Func<string, string> localize)
    {
        SectionHeading.Text = localize("RoutingHeading");
        StatusText.Text = localize("SourceStatusPlanned");
        BackendNameText.Text = localize(descriptor.DisplayNameKey);
        BackendDescText.Text = localize(descriptor.SubtitleKey);
        PlannedHintText.Text = localize("PlannedInspectorHint");
    }
}
