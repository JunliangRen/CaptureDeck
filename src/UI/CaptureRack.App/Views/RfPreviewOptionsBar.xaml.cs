using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class RfPreviewOptionsBar : UserControl
{
    private bool hifiFile;
    private bool binding;
    private string rfOnly = "RF";
    internal RfPreviewType PreviewType => PreviewTypeBox.SelectedIndex switch
    {
        1 when hifiFile => RfPreviewType.VhsHifi,
        2 => BetaProfile.SelectedIndex switch
        {
            1 => RfPreviewType.BetamaxHifi,
            _ => RfPreviewType.Betamax
        },
        4 when VhsProfile.Visibility == Visibility.Visible && VhsProfile.SelectedIndex == 1 => RfPreviewType.VhsHifi,
        int index => (RfPreviewType)Math.Max(0, index)
    };
    internal event Action? Changed;
    internal event Action? DecoderChanged;
    internal string AudioTrackLabel => PreviewType == RfPreviewType.VhsHifi ? "VHS Hi-Fi" : PreviewType == RfPreviewType.BetamaxHifi ? "BETAMAX_HIFI" : LdTrack.SelectedItem as string ?? "LD";
    internal bool PalPreview => VideoStandard.SelectedIndex == 1;
    internal int FieldMode { get => FieldView.SelectedIndex; set => FieldView.SelectedIndex = value; }
    internal event Action? DisplayChanged;
    internal bool FullRaster => Region.SelectedIndex == 1;
    internal bool RestoreCvbs => CvbsRestore.IsChecked == true;
    internal int LdAudioIndex => LdTrack.SelectedIndex;

    public RfPreviewOptionsBar()
    {
        InitializeComponent();
        void Select() { if (!binding) { Changed?.Invoke(); DecoderChanged?.Invoke(); } }
        PreviewTypeBox.SelectionChanged += (_, _) => Select();
        BetaProfile.SelectionChanged += (_, _) => Select();
        VhsProfile.SelectionChanged += (_, _) => Select();
        VideoStandard.SelectionChanged += (_, _) => Select();
        Region.SelectionChanged += (_, _) => DisplayChanged?.Invoke();
        FieldView.SelectionChanged += (_, _) => DisplayChanged?.Invoke();
        CvbsRestore.Checked += (_, _) => DecoderChanged?.Invoke();
        CvbsRestore.Unchecked += (_, _) => DecoderChanged?.Invoke();
        LdTrack.SelectionChanged += (_, _) => { if (!binding) DecoderChanged?.Invoke(); };
    }

    internal void Bind(Func<string, string> labels)
    {
        int index = Math.Max(0, PreviewTypeBox.SelectedIndex);
        TypeLabel.Text = labels("RfPreviewType");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(PreviewTypeBox, TypeLabel);
        rfOnly = labels("RfOnly");
        BindTypes(index);
    }

    private void BindTypes(int index)
    {
        binding = true;
        try
        {
            PreviewTypeBox.ItemsSource = hifiFile ? new[] { rfOnly, "VHS Hi-Fi" } : new[] { rfOnly, "CVBS", "Betamax", "LD", "VHS" };
            PreviewTypeBox.SelectedIndex = index;
        }
        finally { binding = false; }
    }

    internal void SelectHifiFile(bool identified, uint center)
    {
        if (!hifiFile) return;
        PreviewTypeBox.SelectedIndex = identified ? 1 : 0;
        VideoStandard.SelectedIndex = center == 1_600_000 ? 1 : 0;
    }

    internal void Update(bool canDecode, bool available, bool hifi, bool twentyMsps)
    {
        if (hifiFile != hifi)
        {
            hifiFile = hifi;
            BindTypes(0);
        }
        PreviewTypeBox.IsEnabled = available && canDecode;
        if (!canDecode && PreviewTypeBox.SelectedIndex > 0) PreviewTypeBox.SelectedIndex = 0;
        VhsProfile.Visibility = !hifiFile && twentyMsps && PreviewTypeBox.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        bool standardSelectable = PreviewType is RfPreviewType.VideoOnly or RfPreviewType.Vhs or RfPreviewType.VhsHifi;
        FieldView.Visibility = !hifiFile && canDecode && PreviewType is not (RfPreviewType.RfOnly or RfPreviewType.VhsHifi)
            ? Visibility.Visible : Visibility.Collapsed;
        VideoStandard.Visibility = standardSelectable ? Visibility.Visible : Visibility.Collapsed;
        CvbsRestore.Visibility = PreviewType == RfPreviewType.VideoOnly ? Visibility.Visible : Visibility.Collapsed;
        LdTrack.Visibility = PreviewType == RfPreviewType.LaserDisc ? Visibility.Visible : Visibility.Collapsed;
        Region.Visibility = FieldView.Visibility;
        BetaProfile.Visibility = PreviewTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        BetaProfile.IsEnabled = VideoStandard.IsEnabled = CvbsRestore.IsEnabled = available && canDecode;
        LdTrack.IsEnabled = canDecode && available;
    }
}
