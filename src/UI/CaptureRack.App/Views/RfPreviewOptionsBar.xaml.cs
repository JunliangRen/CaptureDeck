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
        2 => BetaProfile.SelectedIndex is 1 or 3 or 4 && BetaMonitorAudio.IsChecked == true
            ? RfPreviewType.BetamaxHifi : RfPreviewType.Betamax,
        4 when VhsStandard.SelectedIndex == 2 => RfPreviewType.SecamVhs,
        4 when VhsStandard.SelectedIndex == 3 => RfPreviewType.MesecamVhs,
        4 when VhsProfile.SelectedIndex == 1 => RfPreviewType.SVhs,
        4 when VhsProfile.SelectedIndex == 2 => RfPreviewType.VhsHifi,
        int index => (RfPreviewType)Math.Max(0, index)
    };

    public bool HasOpenDropdown =>
        PreviewTypeBox.IsDropDownOpen ||
        VideoStandard.IsDropDownOpen || VhsStandard.IsDropDownOpen ||
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
        RfPreviewType.BetamaxHifi => (BetaProfile.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? localize("RfBetamaxHifiLabel"),
        _ => (LdTrack.SelectedItem as ComboBoxItem)?.Content as string ?? "LD"
    };

    internal int Threads => double.IsFinite(ThreadBudget.Value) ? (int)Math.Clamp(Math.Round(ThreadBudget.Value), 1, 64) : Math.Min(4, Environment.ProcessorCount);
    internal int Ac3DelayMs => double.IsFinite(Ac3Delay.Value) ? (int)Math.Clamp(Math.Round(Ac3Delay.Value), int.MinValue, int.MaxValue) : 128;
    internal int BetaProfileIndex => Math.Max(0, BetaProfile.SelectedIndex);
    internal bool HasAudio => PreviewType is RfPreviewType.VhsHifi or RfPreviewType.BetamaxHifi ||
        PreviewType == RfPreviewType.LaserDisc && LdAudioIndex != 4;
    private int StandardIndex => (PreviewTypeBox.SelectedIndex == 4 ? VhsStandard : VideoStandard).SelectedIndex;
    internal bool PalPreview => StandardIndex == 1;
    internal bool HasNtscPicture => FullscreenRenderPolicy.IsNtscRfPicture(PreviewType, StandardIndex);
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
        ThreadBudget.Value = Math.Min(4, Environment.ProcessorCount);
        ThreadBudget.ValueChanged += (_, _) => Select();
        Ac3Delay.ValueChanged += (_, _) => Select();
        PreviewTypeBox.SelectionChanged += (_, _) =>
        {
            if (!binding && !hifiFile && PreviewTypeBox.SelectedIndex >= 0)
                preferredVideoPreviewTypeIndex = PreviewTypeBox.SelectedIndex;
            Select();
        };
        BetaMonitorAudio.Click += (_, _) => Select();
        BetaProfile.SelectionChanged += (_, _) => Select();
        VhsStandard.SelectionChanged += (_, _) => Select();
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
            Changed?.Invoke();
            DecoderChanged?.Invoke();
            DisplayChanged?.Invoke();
        };
    }

    internal void RestoreSettings(RfSettings settings)
    {
        binding = true;
        try
        {
            pendingPreviewTypeIndex = Math.Clamp(settings.PreviewTypeIndex, 0, 4);
            preferredVideoPreviewTypeIndex = pendingPreviewTypeIndex.Value;
            if (PreviewTypeBox.Items.Count > 0)
            {
                PreviewTypeBox.SelectedIndex = Math.Clamp(pendingPreviewTypeIndex.Value, 0, PreviewTypeBox.Items.Count - 1);
                pendingPreviewTypeIndex = null;
            }
            VideoStandard.SelectedIndex = settings.PalPreview ? 1 : 0;
            VhsStandard.SelectedIndex = settings.PreviewTypeIndex switch
            {
                6 => 2, 7 => 3, _ => Math.Clamp(settings.VhsStandardIndex ?? (settings.PalPreview ? 1 : 0), 0, 3)
            };
            VhsProfile.SelectedIndex = settings.PreviewTypeIndex switch
            {
                5 => 1, 6 or 7 => 0,
                _ => settings.VhsProfileIndex switch { 1 => 2, 2 => 1, _ => 0 }
            };
            BetaProfile.SelectedIndex = Math.Clamp(settings.BetaProfileIndex, 0, 4);
            ThreadBudget.Value = Math.Clamp(settings.PreviewDecodeThreads, 1, 64);
            Ac3Delay.Value = settings.Ac3DelayMs;
            BetaMonitorAudio.IsChecked = settings.BetaMonitorAudio;
            LdTrack.SelectedIndex = Math.Clamp(settings.LdTrackIndex, 0, 4);
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
        PalPreview = VideoStandard.SelectedIndex == 1,
        VhsStandardIndex = VhsStandard.SelectedIndex,
        VhsProfileIndex = VhsProfile.SelectedIndex switch { 1 => 2, 2 => 1, _ => 0 },
        BetaProfileIndex = Math.Max(0, BetaProfile.SelectedIndex),
        PreviewDecodeThreads = Threads,
        Ac3DelayMs = Ac3DelayMs,
        BetaMonitorAudio = BetaMonitorAudio.IsChecked == true,
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

        ThreadBudget.Header = labels("RfPreviewThreads");
        Ac3Delay.Header = labels("RfAc3Delay");
        DecoderOptionsLabel.Text = labels("RfDecoderOptionsButton");
        AutomationProperties.SetName(VhsStandard, labels("RfStandardLabel"));
        FieldViewLabel.Text = labels("RfFieldViewLabel");
        RegionLabel.Text = labels("RfRegionLabel");
        AutomationProperties.SetName(VhsProfile, labels("RfTrackLabel"));
        AutomationProperties.SetName(FieldView, labels("RfFieldViewLabel"));
        AutomationProperties.SetName(Region, labels("RfRegionLabel"));

        binding = true;
        try
        {
            BetaHifiItem.Content = labels("RfBetamaxHifiLabel");
            LdSilentItem.Content = labels("RfVideoOnlyTrack");
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

    internal void Update(bool canDecode, bool available, bool hifi, int rate)
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

            bool showVhsProfile = !hifiFile && PreviewTypeBox.SelectedIndex == 4;
            VhsStandard.IsEnabled = enabled;
            VhsStandard.Visibility = VhsProfile.Visibility = showVhsProfile ? Visibility.Visible : Visibility.Collapsed;

            bool beta = PreviewTypeBox.SelectedIndex == 2;
            bool ld = PreviewType == RfPreviewType.LaserDisc;
            bool standardSelectable = !showVhsProfile && PreviewType != RfPreviewType.RfOnly;
            SuperVhsItem.IsEnabled = VhsHifiItem.IsEnabled = VhsStandard.SelectedIndex < 2;
            if (showVhsProfile && VhsStandard.SelectedIndex >= 2) VhsProfile.SelectedIndex = 0;
#if REALTIME_RF
            bool Supports(RfPreviewType type, int profile, int track) =>
                VHSDecode.Preview.RealtimePreviewCapabilities.IsSupported(
                    RfDecoderOptions.Create(type, rate, PalPreview, false, profile, track));
            if (beta && canDecode)
            {
                for (int i = 0; i < BetaProfile.Items.Count; i++)
                    ((ComboBoxItem)BetaProfile.Items[i]).IsEnabled = Supports(RfPreviewType.Betamax, i, 4);
                if (BetaProfile.SelectedItem is not ComboBoxItem { IsEnabled: true }) BetaProfile.SelectedIndex = 0;
            }
            if (ld && canDecode)
            {
                for (int i = 0; i < LdTrack.Items.Count; i++)
                    ((ComboBoxItem)LdTrack.Items[i]).IsEnabled = Supports(RfPreviewType.LaserDisc, 0, i);
                if (LdTrack.SelectedItem is not ComboBoxItem { IsEnabled: true }) LdTrack.SelectedIndex = 1;
            }
#endif
            Ac3Delay.Visibility = ld && LdAudioIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            BetaMonitorAudio.Visibility = beta && BetaProfileIndex is 1 or 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
            BetaMonitorAudio.IsEnabled = enabled;
            bool showFieldAndRegion = !hifiFile && canDecode && PreviewType is not (RfPreviewType.RfOnly or RfPreviewType.VhsHifi);
            bool showCvbs = PreviewType == RfPreviewType.VideoOnly;

            FieldViewPanel.Visibility = showFieldAndRegion ? Visibility.Visible : Visibility.Collapsed;
            RegionPanel.Visibility = showFieldAndRegion ? Visibility.Visible : Visibility.Collapsed;
            CvbsRestore.Visibility = showCvbs ? Visibility.Visible : Visibility.Collapsed;

            VideoStandard.Visibility = standardSelectable ? Visibility.Visible : Visibility.Collapsed;
            LdTrack.Visibility = PreviewType == RfPreviewType.LaserDisc ? Visibility.Visible : Visibility.Collapsed;
            BetaProfile.Visibility = PreviewTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

            bool hasSecondaryOptions = canDecode && PreviewType != RfPreviewType.RfOnly;
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
        var custom = (FieldViewPanel.Visibility == Visibility.Visible && FieldView.SelectedIndex > 0) ||
            (RegionPanel.Visibility == Visibility.Visible && Region.SelectedIndex > 0) ||
            (CvbsRestore.Visibility == Visibility.Visible && CvbsRestore.IsChecked == true);
        DecoderOverrideDot.Opacity = custom ? 1 : 0;
    }
}
