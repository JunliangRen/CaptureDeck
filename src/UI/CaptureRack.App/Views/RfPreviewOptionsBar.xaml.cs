using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class RfPreviewOptionsBar : UserControl
{
    private bool hifiFile;
    private bool binding;
    private int? pendingPreviewTypeIndex;
    private int preferredVideoPreviewTypeIndex;
    private string rfOnly = "RF";
    private Func<string, string> localize = key => key;

    internal RfPreviewType PreviewType => PreviewTypeBox.SelectedIndex switch
    {
        1 when hifiFile => RfPreviewType.VhsHifi,
        2 => BetaProfile.SelectedIndex switch
        {
            1 => RfPreviewType.BetamaxHifi,
            _ => RfPreviewType.Betamax
        },
        4 when VhsProfilePanel.Visibility == Visibility.Visible && VhsProfile.SelectedIndex == 1 => RfPreviewType.VhsHifi,
        int index => (RfPreviewType)Math.Max(0, index)
    };

    public bool HasOpenDropdown =>
        PreviewTypeBox.IsDropDownOpen ||
        VideoStandard.IsDropDownOpen ||
        BetaProfile.IsDropDownOpen ||
        LdTrack.IsDropDownOpen ||
        VhsProfile.IsDropDownOpen ||
        FieldView.IsDropDownOpen ||
        Region.IsDropDownOpen;

    internal event Action? Changed;
    internal event Action? DecoderChanged;
    internal event Action? DisplayChanged;

    internal string AudioTrackLabel => PreviewType switch
    {
        RfPreviewType.VhsHifi => "VHS Hi-Fi",
        RfPreviewType.BetamaxHifi => localize("RfBetamaxHifiLabel"),
        _ => (LdTrack.SelectedItem as ComboBoxItem)?.Content as string ?? "LD"
    };

    internal bool PalPreview => VideoStandard.SelectedIndex == 1;
    internal int FieldMode { get => FieldView.SelectedIndex; set => FieldView.SelectedIndex = Math.Clamp(value, 0, 3); }
    internal bool FullRaster => Region.SelectedIndex == 1;
    internal bool RestoreCvbs => CvbsRestore.IsChecked == true;
    internal int LdAudioIndex => Math.Max(0, LdTrack.SelectedIndex);

    public RfPreviewOptionsBar()
    {
        InitializeComponent();
        void Select()
        {
            if (binding) return;
            UpdateOverrideDot();
            Changed?.Invoke();
            DecoderChanged?.Invoke();
        }
        PreviewTypeBox.SelectionChanged += (_, _) =>
        {
            if (!binding && !hifiFile && PreviewTypeBox.SelectedIndex >= 0)
                preferredVideoPreviewTypeIndex = PreviewTypeBox.SelectedIndex;
            Select();
        };
        BetaProfile.SelectionChanged += (_, _) => Select();
        VhsProfile.SelectionChanged += (_, _) => Select();
        VideoStandard.SelectionChanged += (_, _) => Select();
        Region.SelectionChanged += (_, _) =>
        {
            if (binding) return;
            UpdateOverrideDot();
            DisplayChanged?.Invoke();
        };
        FieldView.SelectionChanged += (_, _) =>
        {
            if (binding) return;
            UpdateOverrideDot();
            DisplayChanged?.Invoke();
        };
        CvbsRestore.Checked += (_, _) =>
        {
            if (binding) return;
            UpdateOverrideDot();
            DecoderChanged?.Invoke();
            DisplayChanged?.Invoke();
        };
        CvbsRestore.Unchecked += (_, _) =>
        {
            if (binding) return;
            UpdateOverrideDot();
            DecoderChanged?.Invoke();
            DisplayChanged?.Invoke();
        };
        LdTrack.SelectionChanged += (_, _) =>
        {
            if (binding) return;
            DecoderChanged?.Invoke();
            DisplayChanged?.Invoke();
        };
    }

    internal void RestoreSettings(RfSettings settings)
    {
        binding = true;
        try
        {
            pendingPreviewTypeIndex = Math.Max(0, settings.PreviewTypeIndex);
            preferredVideoPreviewTypeIndex = pendingPreviewTypeIndex.Value;
            if (PreviewTypeBox.Items.Count > 0)
            {
                PreviewTypeBox.SelectedIndex = Math.Clamp(pendingPreviewTypeIndex.Value, 0, PreviewTypeBox.Items.Count - 1);
                pendingPreviewTypeIndex = null;
            }
            VideoStandard.SelectedIndex = settings.PalPreview ? 1 : 0;
            VhsProfile.SelectedIndex = Math.Clamp(settings.VhsProfileIndex, 0, 1);
            BetaProfile.SelectedIndex = Math.Clamp(settings.BetaProfileIndex, 0, 1);
            LdTrack.SelectedIndex = Math.Clamp(settings.LdTrackIndex, 0, 3);
            FieldView.SelectedIndex = Math.Clamp(settings.FieldViewIndex, 0, 3);
            Region.SelectedIndex = settings.FullRaster ? 1 : 0;
            CvbsRestore.IsChecked = settings.CvbsRestore;
        }
        finally
        {
            binding = false;
        }
        UpdateOverrideDot();
    }

    internal RfSettings WriteSettings(RfSettings current) => current with
    {
        PreviewTypeIndex = !hifiFile && PreviewTypeBox.IsEnabled && PreviewTypeBox.Items.Count > 0
            ? Math.Max(0, PreviewTypeBox.SelectedIndex)
            : preferredVideoPreviewTypeIndex,
        PalPreview = PalPreview,
        VhsProfileIndex = Math.Max(0, VhsProfile.SelectedIndex),
        BetaProfileIndex = Math.Max(0, BetaProfile.SelectedIndex),
        LdTrackIndex = LdAudioIndex,
        FieldViewIndex = Math.Max(0, FieldView.SelectedIndex),
        FullRaster = FullRaster,
        CvbsRestore = RestoreCvbs
    };

    internal void Bind(Func<string, string> labels)
    {
        localize = labels;
        int index = pendingPreviewTypeIndex ?? Math.Max(0, PreviewTypeBox.SelectedIndex);
        pendingPreviewTypeIndex = null;
        rfOnly = labels("RfOnly");

        ToolTipService.SetToolTip(PreviewTypeBox, labels("RfPreviewType"));
        AutomationProperties.SetName(PreviewTypeBox, labels("RfPreviewType"));
        ToolTipService.SetToolTip(VideoStandard, labels("RfStandardLabel"));
        AutomationProperties.SetName(VideoStandard, labels("RfStandardLabel"));
        ToolTipService.SetToolTip(BetaProfile, labels("RfTrackLabel"));
        AutomationProperties.SetName(BetaProfile, labels("RfTrackLabel"));
        ToolTipService.SetToolTip(LdTrack, labels("RfTrackLabel"));
        AutomationProperties.SetName(LdTrack, labels("RfTrackLabel"));

        DecoderOptionsLabel.Text = labels("RfDecoderOptionsButton");
        VhsProfileLabel.Text = labels("RfTrackLabel");
        FieldViewLabel.Text = labels("RfFieldViewLabel");
        RegionLabel.Text = labels("RfRegionLabel");
        AutomationProperties.SetName(VhsProfile, labels("RfTrackLabel"));
        AutomationProperties.SetName(FieldView, labels("RfFieldViewLabel"));
        AutomationProperties.SetName(Region, labels("RfRegionLabel"));

        binding = true;
        try
        {
            BetaHifiItem.Content = labels("RfBetamaxHifiLabel");
            LdAnalogItem.Content = labels("RfLdAnalog");
            LdPcmItem.Content = labels("RfLdDigitalPcm");
            LdDtsItem.Content = labels("RfLdDts");
            LdAc3Item.Content = labels("RfLdAc3");

            FieldFrameItem.Content = labels("RfFieldFrame");
            FieldSplitItem.Content = labels("RfFieldSplit");
            FieldTopItem.Content = labels("RfFieldTop");
            FieldBottomItem.Content = labels("RfFieldBottom");

            RegionActiveItem.Content = labels("RfRegionActive");
            RegionFullItem.Content = labels("RfRegionFull");

            // Refresh closed ComboBox text presenters after Content updates.
            RefreshComboDisplay(BetaProfile);
            RefreshComboDisplay(LdTrack);
            RefreshComboDisplay(FieldView);
            RefreshComboDisplay(Region);
            BindTypes(index);
        }
        finally
        {
            binding = false;
        }
        UpdateOverrideDot();
    }

    private static void RefreshComboDisplay(ComboBox box)
    {
        var idx = box.SelectedIndex;
        if (idx >= 0)
        {
            box.SelectedIndex = -1;
            box.SelectedIndex = idx;
        }
    }

    private void BindTypes(int index)
    {
        var wasBinding = binding;
        binding = true;
        try
        {
            var items = hifiFile ? new[] { rfOnly, "VHS Hi-Fi" } : new[] { rfOnly, "CVBS", "Betamax", "LD", "VHS" };
            PreviewTypeBox.ItemsSource = items;
            PreviewTypeBox.SelectedIndex = Math.Clamp(index, 0, items.Length - 1);
        }
        finally
        {
            binding = wasBinding;
        }
    }

    internal void SelectHifiFile(bool identified, uint center)
    {
        if (!hifiFile) return;
        PreviewTypeBox.SelectedIndex = identified ? 1 : 0;
        VideoStandard.SelectedIndex = center == 1_600_000 ? 1 : 0;
    }

    internal void Update(bool canDecode, bool available, bool hifi, bool twentyMsps)
    {
        var wasBinding = binding;
        binding = true;
        try
        {
            if (hifiFile != hifi)
            {
                hifiFile = hifi;
                BindTypes(hifi ? 0 : preferredVideoPreviewTypeIndex);
            }

            var enabled = available && canDecode;
            PreviewTypeBox.IsEnabled = enabled;
            if (available)
            {
                if (!canDecode && PreviewTypeBox.SelectedIndex > 0)
                    PreviewTypeBox.SelectedIndex = 0;
                else if (canDecode && !hifiFile && PreviewTypeBox.SelectedIndex == 0 && preferredVideoPreviewTypeIndex > 0 && PreviewTypeBox.Items.Count > 0)
                    PreviewTypeBox.SelectedIndex = Math.Clamp(preferredVideoPreviewTypeIndex, 0, PreviewTypeBox.Items.Count - 1);
            }

            bool showVhsProfile = !hifiFile && twentyMsps && PreviewTypeBox.SelectedIndex == 4;
            VhsProfilePanel.Visibility = showVhsProfile ? Visibility.Visible : Visibility.Collapsed;

            bool standardSelectable = PreviewType is RfPreviewType.VideoOnly or RfPreviewType.Vhs or RfPreviewType.VhsHifi;
            bool showFieldAndRegion = !hifiFile && canDecode && PreviewType is not (RfPreviewType.RfOnly or RfPreviewType.VhsHifi);
            bool showCvbs = PreviewType == RfPreviewType.VideoOnly;

            FieldViewPanel.Visibility = showFieldAndRegion ? Visibility.Visible : Visibility.Collapsed;
            RegionPanel.Visibility = showFieldAndRegion ? Visibility.Visible : Visibility.Collapsed;
            CvbsRestore.Visibility = showCvbs ? Visibility.Visible : Visibility.Collapsed;

            VideoStandard.Visibility = standardSelectable ? Visibility.Visible : Visibility.Collapsed;
            LdTrack.Visibility = PreviewType == RfPreviewType.LaserDisc ? Visibility.Visible : Visibility.Collapsed;
            BetaProfile.Visibility = PreviewTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

            bool hasSecondaryOptions = showVhsProfile || showFieldAndRegion || showCvbs;
            DecoderOptionsButton.Visibility = hasSecondaryOptions ? Visibility.Visible : Visibility.Collapsed;
            DecoderOptionsButton.IsEnabled = enabled;

            BetaProfile.IsEnabled = VideoStandard.IsEnabled = CvbsRestore.IsEnabled =
                VhsProfile.IsEnabled = FieldView.IsEnabled = Region.IsEnabled = LdTrack.IsEnabled = enabled;
        }
        finally
        {
            binding = wasBinding;
        }

        UpdateOverrideDot();
    }

    private void UpdateOverrideDot()
    {
        var custom = (VhsProfilePanel.Visibility == Visibility.Visible && VhsProfile.SelectedIndex > 0) ||
            (FieldViewPanel.Visibility == Visibility.Visible && FieldView.SelectedIndex > 0) ||
            (RegionPanel.Visibility == Visibility.Visible && Region.SelectedIndex > 0) ||
            (CvbsRestore.Visibility == Visibility.Visible && CvbsRestore.IsChecked == true);
        DecoderOverrideDot.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }
}
