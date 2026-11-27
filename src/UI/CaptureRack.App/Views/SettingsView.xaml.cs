using CaptureRack.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CaptureRack.App.Views;

public sealed partial class SettingsView : UserControl
{
    private bool syncing;

    public ScrollView Scroller => SettingsScroller;

    public bool HasOpenDropdown =>
        AudioOutputPicker.IsDropDownOpen || ThemePicker.IsDropDownOpen || LanguagePicker.IsDropDownOpen || DefaultLayoutPicker.IsDropDownOpen ||
        SettingsDeinterlacePicker.IsDropDownOpen || SettingsAspectPicker.IsDropDownOpen || SettingsMatrixPicker.IsDropDownOpen;

    public event EventHandler? BackRequested;
    public event EventHandler<string>? ThemeChanged;
    public event EventHandler<string>? LanguageChanged;
    public event EventHandler<string>? DefaultLayoutChanged;
    public event EventHandler<string>? PreviewDeinterlaceChanged;
    public event EventHandler<string>? PreviewAspectChanged;
    public event EventHandler<string>? PreviewMatrixChanged;
    public event EventHandler? RefreshToolsRequested;
    public event EventHandler? OpenSettingsFolderRequested;

    public event EventHandler? AudioOutputsRequested;
    public event EventHandler<PlaybackDevice>? AudioOutputChanged;
    private bool updatingOutputs;

    public SettingsView()
    {
        InitializeComponent();
        AudioOutputPicker.DropDownOpened += (_, _) => AudioOutputsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowAudioOutputs(IReadOnlyList<PlaybackDevice> devices, string selectedId, string storedName, Func<string,string> localize)
    {
        var choices = new List<PlaybackDevice> { new("", localize("AudioOutputDefault")) };
        choices.AddRange(devices);
        var selected = choices.Find(x => x.Id == selectedId);
        if (selected is null)
        {
            selected = new(selectedId, string.Format(localize("AudioOutputUnavailable"),
                string.IsNullOrEmpty(storedName) ? selectedId : storedName));
            choices.Add(selected);
        }
        if (AudioOutputPicker.ItemsSource is List<PlaybackDevice> old && old.SequenceEqual(choices) &&
            Equals(AudioOutputPicker.SelectedItem, selected)) return;
        updatingOutputs = true;
        try
        {
            AudioOutputPicker.ItemsSource = choices;
            AudioOutputPicker.SelectedItem = selected;
        }
        finally { updatingOutputs = false; }
    }

    private void AudioOutputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingOutputs && AudioOutputPicker.SelectedItem is PlaybackDevice device)
            AudioOutputChanged?.Invoke(this, device);
    }

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    public void SyncSelections(string theme, string language, string layout,
        string deinterlace, bool deinterlaceLocked, string aspect, string matrix)
    {
        syncing = true;
        try
        {
            SelectTag(ThemePicker, theme, ThemeSystemItem);
            SelectTag(LanguagePicker, language, LanguagePicker.Items.OfType<ComboBoxItem>().First());
            SelectTag(DefaultLayoutPicker, layout, DefaultLayoutSplitItem);
            SettingsDeinterlacePicker.IsEnabled = !deinterlaceLocked;
            if (deinterlaceLocked)
                SettingsDeinterlacePicker.IsDropDownOpen = false;
            SelectTag(SettingsDeinterlacePicker, deinterlaceLocked ? "Off" : deinterlace, DeinterlaceBobWeaveItem);
            SelectTag(SettingsAspectPicker, aspect, AspectAutoItem);
            SelectTag(SettingsMatrixPicker, matrix, MatrixAutoItem);
        }
        finally
        {
            syncing = false;
        }
    }

    public void ApplyLanguage(Func<string, string> localize, string theme, string language, string layout,
        string deinterlace, bool deinterlaceLocked, string aspect, string matrix)
    {
        SettingsHeading.Text = localize("Settings");
        AutomationProperties.SetName(BackButton, localize("Back"));
        ToolTipService.SetToolTip(BackButton, localize("Back"));

        AppearanceHeading.Text = localize("Appearance");
        ThemeCard.Header = localize("Theme");
        ThemeCard.Description = localize("ThemeDescription");
        ThemeDarkItem.Content = localize("ThemeDark");
        ThemeSystemItem.Content = localize("ThemeSystem");
        ThemeLightItem.Content = localize("ThemeLight");

        LanguageCard.Header = localize("Language");
        AutomationProperties.SetName(LanguagePicker, localize("Language"));
        DefaultLayoutCard.Header = localize("DefaultLayout");
        DefaultLayoutCard.Description = localize("DefaultLayoutDescription");
        DefaultLayoutSplitItem.Content = localize("LayoutSplit");
        DefaultLayoutFocusItem.Content = localize("LayoutFocus");

        AudioOutputCard.Header = localize("AudioOutputLabel");
        AudioOutputCard.Description = localize("AudioOutputDescription");
        AutomationProperties.SetName(AudioOutputPicker, localize("AudioOutputLabel"));
        PreviewDefaultsHeading.Text = localize("PreviewDefaultsHeading");
        PreviewDefaultsSubtitle.Text = localize("PreviewOnlyNotice");
        SettingsDeinterlaceCard.Header = localize("PreviewDeinterlaceLabel");
        SettingsDeinterlaceCard.Description = localize("PreviewDeinterlaceDesc");
        DeinterlaceBobWeaveItem.Content = localize("DeinterlaceBobWeave");
        DeinterlaceBobWeave2xItem.Content = localize("DeinterlaceBobWeave2x");
        DeinterlaceOffItem.Content = localize("DeinterlaceOff");

        SettingsAspectCard.Header = localize("PreviewAspectLabel");
        SettingsAspectCard.Description = localize("PreviewAspectDesc");
        AspectAutoItem.Content = localize("AspectAuto");
        Aspect4x3Item.Content = localize("Aspect4x3");
        Aspect16x9Item.Content = localize("Aspect16x9");
        Aspect1x1Item.Content = localize("Aspect1x1");

        SettingsMatrixCard.Header = localize("PreviewMatrixLabel");
        SettingsMatrixCard.Description = localize("PreviewMatrixDesc");
        MatrixAutoItem.Content = localize("MatrixAuto");
        Matrix601Item.Content = localize("MatrixBt601");
        Matrix709Item.Content = localize("MatrixBt709");

        ToolchainHeading.Text = localize("ToolchainHeading");
        ToolchainCard.Header = localize("ToolchainCardHeader");
        ShortcutsHeading.Text = localize("ShortcutsHeading");
        ShortcutPreviewLabel.Text = localize("ShortcutPreview");
        ShortcutRecordLabel.Text = localize("ShortcutRecord");
        ShortcutFullscreenLabel.Text = localize("PreviewFullScreen");
        ShortcutInspectorLabel.Text = localize("ShortcutInspector");
        ShortcutLayoutsLabel.Text = localize("ShortcutLayouts");
        ShortcutAudioMuteLabel.Text = localize("ShortcutAudioMute");
        ShortcutRfPlayPauseLabel.Text = localize("ShortcutRfPlayPause");
        ShortcutRfStepFieldLabel.Text = localize("ShortcutRfStepField");
        ShortcutRfOpenFileLabel.Text = localize("ShortcutRfOpenFile");
        ShortcutBackLabel.Text = localize("Back");

        AboutHeading.Text = localize("About");
        OpenSettingsFolderButton.Content = localize("OpenSettingsFolder");

        // Re-select all ComboBoxes so WinUI's closed ContentPresenter refreshes immediately.
        SyncSelections(theme, language, layout, deinterlace, deinterlaceLocked, aspect, matrix);
    }

    public void ShowToolchainStatus(bool ready, string statusText, string description)
    {
        ToolchainStatusText.Text = statusText;
        ToolchainStatusText.Foreground = StudioBrushes.Resolve(this, ready ? "StudioSignalLockBrush" : "StudioCautionBrush");
        ToolchainStatusPill.Background = StudioBrushes.Resolve(this, ready ? "StudioSignalLockSoftBrush" : "StudioCautionSoftBrush");
        ToolchainCard.Description = description;
    }

    public void ShowAboutDescription(string description) => AboutCard.Description = description;

    public void ShowNotice(string title, string message, InfoBarSeverity severity, bool isOpen)
    {
        SettingsNotice.Title = title;
        SettingsNotice.Message = message;
        SettingsNotice.Severity = severity;
        SettingsNotice.IsOpen = isOpen;
    }

    private static void SelectTag(ComboBox picker, string tag, ComboBoxItem fallback)
    {
        picker.SelectedIndex = -1;
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && ThemePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            ThemeChanged?.Invoke(this, tag);
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && LanguagePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            LanguageChanged?.Invoke(this, tag);
    }

    private void DefaultLayoutPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && DefaultLayoutPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            DefaultLayoutChanged?.Invoke(this, tag);
    }

    private void DeinterlacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && SettingsDeinterlacePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            PreviewDeinterlaceChanged?.Invoke(this, tag);
    }

    private void AspectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && SettingsAspectPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            PreviewAspectChanged?.Invoke(this, tag);
    }

    private void MatrixPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && SettingsMatrixPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            PreviewMatrixChanged?.Invoke(this, tag);
    }

    private void RefreshTools_Click(object sender, RoutedEventArgs e) => RefreshToolsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => OpenSettingsFolderRequested?.Invoke(this, EventArgs.Empty);

    private void ShortcutsCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var singleColumn = e.NewSize.Width > 0 && e.NewSize.Width < 560;
        ShortcutsSecondColumn.Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ShortcutsGrid.ColumnSpacing = singleColumn ? 0 : 24;
        ShortcutsGrid.RowSpacing = singleColumn ? 8 : 0;
        Grid.SetColumn(ShortcutsRightColumn, singleColumn ? 0 : 1);
        Grid.SetRow(ShortcutsRightColumn, singleColumn ? 1 : 0);
    }
}
