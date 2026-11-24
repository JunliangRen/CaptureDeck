using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text.Json;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private static readonly ResourceManager SettingsText = new("CaptureRack.App.SettingsStrings", typeof(MainWindow).Assembly);
    private CaptureSettings settings = new();
    private CultureInfo settingsCulture = CultureInfo.GetCultureInfo("en");
    private DispatcherQueueTimer settingsSaveTimer = null!;
    private DispatcherQueueTimer storageTimer = null!;
    private bool settingsReady;
    private bool settingsPending;
    private (string Key, string Message)? settingsError;
    private bool checkingStorage;
    private string? checkedFolder;
    private (ulong AvailableBytes, ulong TotalBytes)? diskSpace;

    private void InitializeSettings()
    {
        settingsSaveTimer = DispatcherQueue.CreateTimer();
        settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
        settingsSaveTimer.IsRepeating = false;
        settingsSaveTimer.Tick += (_, _) => SavePendingSettings();
        storageTimer = DispatcherQueue.CreateTimer();
        storageTimer.Interval = TimeSpan.FromSeconds(10);
        storageTimer.Tick += async (_, _) => await RefreshStorageAsync();
        ApplyThemeSelection("System");
        ApplySettingsLanguage(settingsCulture.Name, (uint)CapturePixelFormat.V210, (uint)CaptureAudioFormat.Pcm16);
        UpdateConfigurationStatus();
    }

    private async Task RestoreSettingsAsync()
    {
        loading = true;
        var loaded = false;
        try
        {
            settings = await Task.Run(CaptureSettingsStore.Load);
            loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowSettingsError("ReadError", ex.Message);
        }

        if (closing)
            return;

        var language = SettingsLanguage.Resolve(settings.LanguageOverride, CultureInfo.CurrentUICulture.Name);
        ApplyThemeSelection(settings.Theme);
        workspaceLayout = string.Equals(settings.WorkspaceLayout, "Focus", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceLayoutMode.Focus : WorkspaceLayoutMode.Split;
        inspectorPinned = settings.InspectorPinned;
        showSafeArea = settings.ShowSafeArea;
        showNtsc480Guide = settings.ShowNtsc480Guide;
        previewDeinterlace = Enum.TryParse<PreviewDeinterlaceMode>(settings.PreviewDeinterlace, out var deint)
            ? deint : PreviewDeinterlaceMode.BobWeave;
        previewAspectMode = Enum.TryParse<PreviewAspectMode>(settings.PreviewAspectRatio, out var aspect)
            ? aspect : PreviewAspectMode.Auto;
        previewColorMatrix = Enum.TryParse<PreviewColorMatrix>(settings.PreviewColorMatrix, out var matrix)
            ? matrix : PreviewColorMatrix.Auto;
        audioMonitorEnabled = settings.AudioMonitorEnabled;
        audioMonitorVolume = Math.Clamp(settings.AudioMonitorVolume, 0.0, 1.0);
        analogNtscIre = settings.DeckLink.AnalogNtscIre;
        analogComponentLevel = settings.DeckLink.AnalogComponentLevel;
        analogAudioLevel = settings.DeckLink.AnalogAudioLevel;
        analogAudioGainOverride = settings.DeckLink.AnalogAudioGainOverride;
        analogAudioGainLeftDb = Math.Clamp(settings.DeckLink.AnalogAudioGainLeftDb, -12, 12);
        analogAudioGainRightDb = Math.Clamp(settings.DeckLink.AnalogAudioGainRightDb, -12, 12);
        analogAudioGainLinked = settings.DeckLink.AnalogAudioGainLinked;
        ApplySettingsLanguage(language, settings.DeckLink.VideoFormat, settings.DeckLink.AudioFormat);
        SelectDefaultLayoutPicker();
        OutputPath.Text = settings.OutputFolder ?? "";
        if (loaded && settings.LanguageOverride != language)
            SaveSettings(settings with { LanguageOverride = language });
        settingsReady = true;
        loading = false;
    }

    private void ApplyThemeSelection(string? theme)
    {
        var normalized = theme is "Dark" or "Light" or "System" ? theme : "System";
        RootShell.RequestedTheme = normalized switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ThemePicker.SelectedItem = ThemePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == normalized);
        UpdateTitleBarTheme();
    }

    private void SelectDefaultLayoutPicker()
    {
        var tag = workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split";
        DefaultLayoutPicker.SelectedItem = DefaultLayoutPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == tag);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsReady || loading || ThemePicker.SelectedItem is not ComboBoxItem { Tag: string theme })
            return;

        ApplyThemeSelection(theme);
        RefreshDynamicThemeBrushes();
        SettingsChanged();
    }

    private void DefaultLayoutPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsReady || loading || DefaultLayoutPicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;

        SetWorkspaceLayout(tag == "Focus" ? WorkspaceLayoutMode.Focus : WorkspaceLayoutMode.Split, saveIfDefaultable: true);
    }

    private void RestoreDeviceSelections(CaptureDevice[] devices)
    {
        var matches = devices.Where(device => settings.DeckLink.DeviceId is { } id
            ? device.PersistentId == id : settings.DeckLink.DeviceName == device.Name).ToArray();
        DevicePicker.SelectedItem = settings.DeckLink.DeviceId is null && settings.DeckLink.DeviceName is null
            ? devices.FirstOrDefault()
            : matches.Length == 1 ? matches[0] : null;

        UpdateSelectedDevice();
        if (DevicePicker.SelectedItem is null)
            return;

        VideoInputPicker.SelectedItem = VideoInputPicker.Items.OfType<VideoInputSource>()
            .FirstOrDefault(source => source.Id == settings.DeckLink.VideoSource);
        AudioInputPicker.SelectedItem = AudioInputPicker.Items.OfType<AudioInputSource>()
            .FirstOrDefault(source => source.Id == settings.DeckLink.AudioSource);
        if (settings.DeckLink.ModeSelected)
            ModePicker.SelectedItem = ModePicker.Items.OfType<ModeOption>().FirstOrDefault(mode => mode.Id == settings.DeckLink.ModeId);
    }

    private void SettingsChanged()
    {
        if (!settingsReady || loading || closing)
            return;

        if (settingsError is { Key: "FolderError" })
            settingsError = null;

        settingsPending = true;
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private void SavePendingSettings()
    {
        if (!settingsPending)
            return;

        settingsSaveTimer.Stop();
        settingsPending = false;
        var deckLink = settings.DeckLink;
        if (DevicePicker.SelectedItem is CaptureDevice device)
        {
            var sameDevice = deckLink.DeviceId is { } id ? device.PersistentId == id : deckLink.DeviceName == device.Name;
            var video = VideoInputPicker.SelectedItem as VideoInputSource;
            var audio = AudioInputPicker.SelectedItem as AudioInputSource;
            var mode = ModePicker.SelectedItem as ModeOption;
            var keepMode = sameDevice && (video is null || video.Id == deckLink.VideoSource) && deckLink.ModeSelected;
            deckLink = deckLink with
            {
                DeviceId = device.PersistentId,
                DeviceName = device.Name,
                VideoSource = video?.Id ?? (sameDevice ? deckLink.VideoSource : null),
                AudioSource = audio?.Id ?? (sameDevice ? deckLink.AudioSource : null),
                ModeSelected = mode is not null || keepMode,
                ModeId = mode is not null ? mode.Id : keepMode ? deckLink.ModeId : null
            };
        }

        deckLink = deckLink with
        {
            VideoFormat = (uint)selectedVideoFormat,
            AudioFormat = (uint)selectedAudioFormat,
            AnalogNtscIre = analogNtscIre,
            AnalogComponentLevel = analogComponentLevel,
            AnalogAudioLevel = analogAudioLevel,
            AnalogAudioGainOverride = analogAudioGainOverride,
            AnalogAudioGainLeftDb = analogAudioGainLeftDb,
            AnalogAudioGainRightDb = analogAudioGainRightDb,
            AnalogAudioGainLinked = analogAudioGainLinked
        };
        var theme = ThemePicker.SelectedItem is ComboBoxItem { Tag: string selectedTheme } ? selectedTheme : settings.Theme;
        var savedLayout = workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split";
        SaveSettings(settings with
        {
            DeckLink = deckLink,
            OutputFolder = OutputPath.Text.Trim(),
            LanguageOverride = settingsCulture.Name,
            Theme = theme,
            WorkspaceLayout = savedLayout,
            InspectorPinned = inspectorPinned,
            ShowSafeArea = showSafeArea,
            ShowNtsc480Guide = showNtsc480Guide,
            PreviewDeinterlace = previewDeinterlace.ToString(),
            PreviewAspectRatio = previewAspectMode.ToString(),
            PreviewColorMatrix = previewColorMatrix.ToString(),
            AudioMonitorEnabled = audioMonitorEnabled,
            AudioMonitorVolume = audioMonitorVolume
        });
        UpdateConfigurationStatus();
        if (!closing && !string.Equals(checkedFolder, OutputPath.Text.Trim(), StringComparison.Ordinal))
            _ = RefreshStorageAsync();
    }

    private void SaveSettings(CaptureSettings updated)
    {
        try
        {
            CaptureSettingsStore.Save(updated);
            settings = updated;
            settingsError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError("SaveError", ex.Message);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (activePage == ActiveAppPage.Settings)
            TryBackFromSettings();
        else
            NavigateToPage(ActiveAppPage.Settings);
    }

    private void BackToWorkspace_Click(object sender, RoutedEventArgs e) => TryBackFromSettings();

    private void SettingsBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = TryBackFromSettings();

    private bool TryBackFromSettings()
    {
        if (activePage != ActiveAppPage.Settings)
            return false;

        NavigateToPage(pageBeforeSettings == ActiveAppPage.Settings ? ActiveAppPage.Capture : pageBeforeSettings);
        OpenSettingsButton.Focus(FocusState.Programmatic);
        return true;
    }

    private void UpdateCaptureFooter()
    {
        var sessionActive = captureTask is not null || recordingTransition || qcStop is not null;
        CaptureFooter.Visibility = !previewFullscreen && (activePage != ActiveAppPage.Settings || sessionActive)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTools_Click(object sender, RoutedEventArgs e) => RefreshToolchainDiagnostics();

    private void RefreshToolchainDiagnostics()
    {
        var (ffmpeg, ffprobe) = QcReports.FindTools();
        var ready = ffmpeg is not null && ffprobe is not null;
        ToolchainStatusText.Text = L(ready ? "ToolchainReady" : "ToolchainMissing");
        ToolchainStatusText.Foreground = ThemeBrush(ready ? "StudioSignalLockBrush" : "StudioCautionBrush");
        ToolchainStatusPill.Background = ThemeBrush(ready ? "StudioSignalLockSoftBrush" : "StudioCautionSoftBrush");
        ToolchainCard.Description = ready
            ? Path.GetDirectoryName(ffmpeg) ?? L("QcToolsDetail")
            : L("QcToolsDetail");
    }

    private async void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPath = Path.GetDirectoryName(CaptureSettingsStore.FilePath)!;
            Directory.CreateDirectory(folderPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            ShowSettingsError("FolderError", ex.Message);
        }
    }

    private string L(string key) => SettingsText.GetString(key, settingsCulture) ?? key;

    private string LocalFormat(string key, params object[] args) => string.Format(settingsCulture, L(key), args);

    private void ApplySettingsLanguage(string language, uint? videoFormat, uint? audioFormat)
    {
        settingsCulture = CultureInfo.GetCultureInfo(language);
        LanguagePicker.SelectedItem = LanguagePicker.Items.OfType<ComboBoxItem>()
            .First(item => (string)item.Tag == language);

        SettingsHeading.Text = L("Settings");
        AutomationProperties.SetName(OpenSettingsButton, L("Settings"));
        ToolTipService.SetToolTip(OpenSettingsButton, L("Settings"));
        AutomationProperties.SetName(BackButton, L("Back"));
        ToolTipService.SetToolTip(BackButton, L("Back"));

        InputHeading.Text = L("Capture");
        RoutingSectionHeading.Text = L("RoutingHeading");
        FormatSectionHeading.Text = L("RecordingFormats");
        OutputHeading.Text = L("Output");
        AppearanceHeading.Text = L("Appearance");
        ToolchainHeading.Text = L("ToolchainHeading");
        ShortcutsHeading.Text = L("ShortcutsHeading");
        AboutHeading.Text = L("About");

        DeviceLabel.Text = L("Device");
        AutomationProperties.SetName(DevicePicker, L("Device"));
        RefreshLabel.Text = L("Refresh");
        VideoSourceLabel.Text = L("VideoSource");
        AutomationProperties.SetName(VideoInputPicker, L("VideoSource"));
        AudioSourceLabel.Text = L("AudioSource");
        AutomationProperties.SetName(AudioInputPicker, L("AudioSource"));
        ModeLabel.Text = L("Mode");
        AutomationProperties.SetName(ModePicker, L("Mode"));
        VideoFormatLabel.Text = L("VideoFormat");
        AudioFormatLabel.Text = L("AudioFormat");
        OutputHelpText.Text = L("OutputHelp");
        AutomationProperties.SetName(OutputPath, L("Folder"));
        OutputPath.PlaceholderText = L("ChooseFolder");
        BrowseLabel.Text = L("Browse");
        RecordingModeHint.Text = L("AutoRecordingHint");

        var selectedThemeTag = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? settings.Theme;
        ThemeCard.Header = L("Theme");
        ThemeCard.Description = L("ThemeDescription");
        ThemeDarkItem.Content = L("ThemeDark");
        ThemeSystemItem.Content = L("ThemeSystem");
        ThemeLightItem.Content = L("ThemeLight");
        // Force WinUI ComboBox ContentPresenter to refresh its cached selected text after mutating item Content.
        ThemePicker.SelectedIndex = -1;
        ThemePicker.SelectedItem = ThemePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == selectedThemeTag) ?? ThemeSystemItem;

        LanguageCard.Header = L("Language");
        AutomationProperties.SetName(LanguagePicker, L("Language"));

        var selectedLayoutTag = (DefaultLayoutPicker.SelectedItem as ComboBoxItem)?.Tag as string
            ?? (workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split");
        DefaultLayoutCard.Header = L("DefaultLayout");
        DefaultLayoutCard.Description = L("DefaultLayoutDescription");
        DefaultLayoutSplitItem.Content = L("LayoutSplit");
        DefaultLayoutFocusItem.Content = L("LayoutFocus");
        DefaultLayoutPicker.SelectedIndex = -1;
        DefaultLayoutPicker.SelectedItem = DefaultLayoutPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == selectedLayoutTag) ?? DefaultLayoutSplitItem;
        ToolchainCard.Header = L("ToolchainCardHeader");
        OpenSettingsFolderButton.Content = L("OpenSettingsFolder");

        ShortcutPreviewLabel.Text = L("ShortcutPreview");
        ShortcutRecordLabel.Text = L("ShortcutRecord");
        ShortcutFullscreenLabel.Text = L("PreviewFullScreen");
        ShortcutInspectorLabel.Text = L("ShortcutInspector");
        ShortcutLayoutsLabel.Text = L("ShortcutLayouts");
        ShortcutBackLabel.Text = L("Back");

        selectedVideoFormat = videoFormat == (uint)CapturePixelFormat.Uyvy ? CapturePixelFormat.Uyvy : CapturePixelFormat.V210;
        selectedAudioFormat = audioFormat == (uint)CaptureAudioFormat.Pcm32 ? CaptureAudioFormat.Pcm32 : CaptureAudioFormat.Pcm16;
        VideoFormat10Button.Content = L("Video10Short");
        VideoFormat8Button.Content = L("Video8Short");
        AudioFormat16Button.Content = L("Audio16Short");
        AudioFormat32Button.Content = L("Audio32Short");
        ToolTipService.SetToolTip(VideoFormat10Button, L("Video10"));
        ToolTipService.SetToolTip(VideoFormat8Button, L("Video8"));
        ToolTipService.SetToolTip(AudioFormat16Button, L("Audio16"));
        ToolTipService.SetToolTip(AudioFormat32Button, L("Audio32"));
        UpdateFormatSegmentVisuals();

        var mode = ModePicker.SelectedItem as ModeOption;
        ApplySignalAndMonitorLanguage();
        ApplyWorkspaceLanguage();
        ModePicker.SelectedItem = mode is null ? null : ModePicker.Items.OfType<ModeOption>()
            .FirstOrDefault(candidate => candidate.Id == mode.Id);
        RefreshToolchainDiagnostics();
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsReady || loading || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        loading = true;
        var videoSource = (VideoInputPicker.SelectedItem as VideoInputSource)?.Id;
        var audioSource = (AudioInputPicker.SelectedItem as AudioInputSource)?.Id;
        var mode = ModePicker.SelectedItem as ModeOption;
        ApplySettingsLanguage(language, (uint)selectedVideoFormat, (uint)selectedAudioFormat);
        UpdateSelectedDevice();
        VideoInputPicker.SelectedItem = VideoInputPicker.Items.OfType<VideoInputSource>()
            .FirstOrDefault(source => source.Id == videoSource);
        AudioInputPicker.SelectedItem = AudioInputPicker.Items.OfType<AudioInputSource>()
            .FirstOrDefault(source => source.Id == audioSource);
        UpdateModeOptions();
        ModePicker.SelectedItem = mode is null ? null : ModePicker.Items.OfType<ModeOption>()
            .FirstOrDefault(candidate => candidate.Id == mode.Id);
        loading = false;
        UpdateControls();
        SettingsChanged();
    }

    private string LocalVideoConnector(VideoInputSource source) => source.Id switch
    {
        4 => L("ConnectorOpticalSdi"),
        8 => L("ConnectorComponent"),
        16 => L("ConnectorComposite"),
        64 => L("ConnectorEthernet"),
        128 => L("ConnectorOpticalEthernet"),
        256 => L("ConnectorInternal"),
        _ => source.Name
    };

    private string LocalAudioConnector(AudioInputSource source) => source.Id switch
    {
        1 => L("ConnectorEmbedded"),
        4 => L("ConnectorAnalog"),
        8 => L("ConnectorXlr"),
        16 => L("ConnectorRca"),
        32 => L("ConnectorMicrophone"),
        64 => L("ConnectorHeadphones"),
        _ => source.Name
    };

    private void ShowSettingsError(string key, string message)
    {
        settingsError = (key, message);
        UpdateConfigurationStatus();
    }

    private void UpdateConfigurationStatus()
    {
        var device = DevicePicker.SelectedItem as CaptureDevice;
        var video = VideoInputPicker.SelectedItem as VideoInputSource;
        var audio = AudioInputPicker.SelectedItem as AudioInputSource;
        var mode = ModePicker.SelectedItem as ModeOption;
        var modeLabel = mode is { Id: null } && observedPreviewMode is { } activeMode
            ? observedPreviewModeIsFallback
                ? LocalFormat("AutoFallbackMode", mode.Label, activeMode.Description)
                : $"{mode.Label} ({activeMode.Description})"
            : mode?.Label;
        var text = device is null || video is null || audio is null || mode is null
            ? L("Configure")
            : $"{device.Name} · {video.Name} / {audio.Name} · {modeLabel} · " +
                $"{LocalFormat("ShortVideoFormat", DeckLinkCapture.FormatFourCc((uint)selectedVideoFormat), selectedVideoFormat == CapturePixelFormat.V210 ? 10 : 8)} / " +
                LocalFormat("ShortPcmFormat", (uint)selectedAudioFormat);
        ConfigurationSummary.Text = text;
        AutomationProperties.SetName(QuickSourceButton, $"{L("Capture")} · {text}");
        ToolTipService.SetToolTip(QuickSourceButton, text);

        var selectedMode = mode?.Id is { } id
            ? video?.ModesFor(selectedVideoFormat).FirstOrDefault(option => option.Id == id)
            : mode is not null ? observedPreviewMode : null;
        if (selectedMode is not null)
        {
            var mbps = DeckLinkCapture.EstimateRecordingBytesPerSecond(selectedMode, selectedVideoFormat, selectedAudioFormat) * 8 / 1_000_000d;
            BitrateBadgeText.Text = $"~{mbps:0.#} Mbps";
            BitrateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            BitrateBadge.Visibility = Visibility.Collapsed;
        }

        var missingSelection = device is not null && ((video is null && settings.DeckLink.VideoSource is not null) ||
            (audio is null && settings.DeckLink.AudioSource is not null) || (mode is null && settings.DeckLink.ModeSelected));
        var message = settingsError is { } error ? error.Message :
            device is null && settings.DeckLink.DeviceName is { } name ? LocalFormat("MissingDevice", name) :
            missingSelection ? L("MissingSelection") : captureTask is not null ? L("ChangeCapture") : "";
        var isWarning = settingsError is not null || missingSelection || device is null;

        SettingsNotice.Title = settingsError is { } failure ? L(failure.Key) : "";
        SettingsNotice.Message = message;
        SettingsNotice.Severity = isWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        SettingsNotice.IsOpen = settingsError is not null;

        InspectorNotice.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        InspectorNoticeText.Text = message;
        InspectorNotice.Background = ThemeBrush(isWarning ? "StudioCautionSoftBrush" : "StudioInfoSoftBrush");
        InspectorNotice.BorderBrush = ThemeBrush(isWarning ? "StudioCautionBrush" : "StudioInfoBrush");
        InspectorNoticeIcon.Foreground = ThemeBrush(isWarning ? "StudioCautionBrush" : "StudioInfoBrush");
        InspectorNoticeIcon.Glyph = isWarning ? "\uE7BA" : "\uE946";

        QuickSourceDot.Fill = device is null
            ? ThemeBrush("StudioTextMutedBrush")
            : captureTask is not null && liveSignal == VideoSignal.Present
                ? ThemeBrush("StudioSignalLockBrush")
                : captureTask is not null && liveSignal == VideoSignal.NoInput
                    ? ThemeBrush("StudioCautionBrush")
                    : ThemeBrush("StudioInfoBrush");

        ShowStorageStatus(selectedMode, mode);
    }

    private async Task RefreshStorageAsync()
    {
        if (checkingStorage || !settingsReady || closing)
            return;

        var directory = OutputPath.Text.Trim();
        if (directory.Length == 0)
            return;

        checkingStorage = true;
        try
        {
            var result = await Task.Run(() => CaptureSettingsStore.DiskSpace(directory));
            if (!closing && string.Equals(directory, OutputPath.Text.Trim(), StringComparison.Ordinal))
            {
                checkedFolder = directory;
                diskSpace = result;
                UpdateConfigurationStatus();
            }
        }
        finally
        {
            checkingStorage = false;
        }
        if (!closing && !string.Equals(directory, OutputPath.Text.Trim(), StringComparison.Ordinal))
            await RefreshStorageAsync();
    }

    private void ShowStorageStatus(VideoMode? selectedMode, ModeOption? mode)
    {
        var directory = OutputPath.Text.Trim();
        if (directory.Length == 0)
        {
            StorageStatus.Text = InspectorStorageStatus.Text = L("ChooseStorage");
            InspectorStorageBar.Visibility = FooterStorageBar.Visibility = Visibility.Collapsed;
        }
        else if (!string.Equals(checkedFolder, directory, StringComparison.Ordinal))
        {
            StorageStatus.Text = InspectorStorageStatus.Text = L("StorageChecking");
            InspectorStorageBar.Visibility = FooterStorageBar.Visibility = Visibility.Collapsed;
        }
        else if (diskSpace is not { } space)
        {
            StorageStatus.Text = InspectorStorageStatus.Text = L("StorageUnavailable");
            InspectorStorageBar.Visibility = FooterStorageBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            var free = LocalFormat("StorageCapacity", (space.AvailableBytes / (double)(1L << 30)).ToString("N1", settingsCulture));
            var estimate = selectedMode is null
                ? L(mode is { Id: null } ? "StorageWaitingForAutoMode" : "StorageNeedsMode")
                : LocalFormat("StorageDuration", FormatRecordingTime(space.AvailableBytes /
                    DeckLinkCapture.EstimateRecordingBytesPerSecond(selectedMode, selectedVideoFormat, selectedAudioFormat)));
            InspectorStorageStatus.Text = $"{free} · {estimate}";
            StorageStatus.Text = selectedMode is not null ? estimate : free;

            if (space.TotalBytes > 0)
            {
                var usedPct = Math.Clamp((1.0 - (double)space.AvailableBytes / space.TotalBytes) * 100.0, 0, 100);
                var freePct = 100.0 - usedPct;
                InspectorStorageBar.Value = FooterStorageBar.Value = usedPct;
                InspectorStorageBar.Visibility = FooterStorageBar.Visibility = Visibility.Visible;
                var barBrush = freePct < 5
                    ? ThemeBrush("StudioDangerBrush")
                    : freePct < 15
                        ? ThemeBrush("StudioCautionBrush")
                        : ThemeBrush("StudioAccentBrush");
                InspectorStorageBar.Foreground = FooterStorageBar.Foreground = barBrush;
            }
        }
        ToolTipService.SetToolTip(FooterStorageButton, directory.Length == 0 ? L("ChooseStorage") : $"{directory}\n{InspectorStorageStatus.Text}");
    }

    private string FormatRecordingTime(double seconds)
    {
        var minutes = (long)Math.Floor(seconds / 60);
        return minutes switch
        {
            < 1 => L("UnderMinute"),
            < 60 => LocalFormat("Minutes", minutes),
            _ => LocalFormat("HoursMinutes", minutes / 60, minutes % 60)
        };
    }

    private void RenderApiVersion()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var sdk = sdkVersion switch
        {
            null => L("ApiWaiting"),
            "" => L("ApiUnavailable"),
            _ => $"Blackmagic DeckLink API {sdkVersion}"
        };
        AboutCard.Description = $"Windows {arch} · {sdk}";
    }
}
