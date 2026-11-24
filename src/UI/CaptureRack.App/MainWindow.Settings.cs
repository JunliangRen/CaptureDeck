using System.Globalization;
using System.Resources;
using System.Text.Json;
using CaptureRack.DeckLink;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
    private ulong? availableBytes;

    private void InitializeSettings()
    {
        settingsSaveTimer = DispatcherQueue.CreateTimer();
        settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
        settingsSaveTimer.IsRepeating = false;
        settingsSaveTimer.Tick += (_, _) => SavePendingSettings();
        storageTimer = DispatcherQueue.CreateTimer();
        storageTimer.Interval = TimeSpan.FromSeconds(10);
        storageTimer.Tick += async (_, _) => await RefreshStorageAsync();
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
        ApplySettingsLanguage(language, settings.DeckLink.VideoFormat, settings.DeckLink.AudioFormat);
        OutputPath.Text = settings.OutputFolder ?? "";
        if (loaded && settings.LanguageOverride != language)
            SaveSettings(settings with { LanguageOverride = language });
        settingsReady = true;
        loading = false;
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
            VideoFormat = FormatPicker.SelectedItem is FormatOption format ? (uint)format.Id : deckLink.VideoFormat,
            AudioFormat = AudioFormatPicker.SelectedItem is AudioFormatOption pcm ? (uint)pcm.Id : deckLink.AudioFormat
        };
        SaveSettings(settings with
        {
            DeckLink = deckLink,
            OutputFolder = OutputPath.Text.Trim(),
            LanguageOverride = settingsCulture.Name
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
        WorkspacePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        StartSettingsWheelFallback();
        UpdateCaptureFooter();
        RevealWorkspacePanel(SettingsPage);
        BackButton.Focus(FocusState.Programmatic);
    }

    private void BackToWorkspace_Click(object sender, RoutedEventArgs e) => TryBackFromSettings();

    private void SettingsBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = TryBackFromSettings();

    private bool TryBackFromSettings()
    {
        if (SettingsPage.Visibility != Visibility.Visible)
            return false;

        SavePendingSettings();
        StopSettingsWheelFallback();
        SettingsPage.Visibility = Visibility.Collapsed;
        WorkspacePage.Visibility = Visibility.Visible;
        UpdateCaptureFooter();
        RevealWorkspacePanel(WorkspacePage);
        OpenSettingsButton.Focus(FocusState.Programmatic);
        return true;
    }

    private void UpdateCaptureFooter()
    {
        var sessionActive = captureTask is not null || recordingTransition || qcStop is not null;
        CaptureFooter.Visibility = !previewFullscreen && (SettingsPage.Visibility == Visibility.Collapsed || sessionActive)
            ? Visibility.Visible : Visibility.Collapsed;
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
        OutputHeading.Text = L("Output");
        AppearanceHeading.Text = L("Appearance");
        AboutHeading.Text = L("About");
        DeviceCard.Header = L("Device");
        AutomationProperties.SetName(DevicePicker, L("Device"));
        RefreshButton.Content = L("Refresh");
        VideoInputCard.Header = L("VideoSource");
        AutomationProperties.SetName(VideoInputPicker, L("VideoSource"));
        AudioInputCard.Header = L("AudioSource");
        AutomationProperties.SetName(AudioInputPicker, L("AudioSource"));
        ModeCard.Header = L("Mode");
        AutomationProperties.SetName(ModePicker, L("Mode"));
        FormatExpander.Header = L("RecordingFormats");
        VideoFormatCard.Header = L("VideoFormat");
        AutomationProperties.SetName(FormatPicker, L("VideoFormat"));
        AudioFormatCard.Header = L("AudioFormat");
        AutomationProperties.SetName(AudioFormatPicker, L("AudioFormat"));
        OutputCard.Header = L("Folder");
        OutputCard.Description = L("OutputHelp");
        AutomationProperties.SetName(OutputPath, L("Folder"));
        OutputPath.PlaceholderText = L("ChooseFolder");
        BrowseButton.Content = L("Browse");
        RecordingModeHint.Text = L("AutoRecordingHint");
        LanguageCard.Header = L("Language");
        AutomationProperties.SetName(LanguagePicker, L("Language"));
        ApplyWorkspaceLanguage();

        var mode = ModePicker.SelectedItem as ModeOption;
        FormatPicker.ItemsSource = new FormatOption[]
        {
            new(L("Video10"), "10bit-Uncompressed", CapturePixelFormat.V210),
            new(L("Video8"), "8bit-Uncompressed", CapturePixelFormat.Uyvy)
        };
        FormatPicker.SelectedIndex = videoFormat == (uint)CapturePixelFormat.Uyvy ? 1 : 0;
        AudioFormatPicker.ItemsSource = new AudioFormatOption[]
        {
            new(L("Audio16"), "16bit-PCM", CaptureAudioFormat.Pcm16),
            new(L("Audio32"), "32bit-PCM", CaptureAudioFormat.Pcm32)
        };
        AudioFormatPicker.SelectedIndex = audioFormat == (uint)CaptureAudioFormat.Pcm32 ? 1 : 0;
        ModePicker.SelectedItem = mode is null ? null : ModePicker.Items.OfType<ModeOption>()
            .FirstOrDefault(candidate => candidate.Id == mode.Id);
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsReady || loading || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        loading = true;
        var videoSource = (VideoInputPicker.SelectedItem as VideoInputSource)?.Id;
        var audioSource = (AudioInputPicker.SelectedItem as AudioInputSource)?.Id;
        var mode = ModePicker.SelectedItem as ModeOption;
        ApplySettingsLanguage(language,
            FormatPicker.SelectedItem is FormatOption format ? (uint)format.Id : null,
            AudioFormatPicker.SelectedItem is AudioFormatOption pcm ? (uint)pcm.Id : null);
        UpdateSelectedDevice();
        VideoInputPicker.SelectedItem = VideoInputPicker.Items.OfType<VideoInputSource>()
            .FirstOrDefault(source => source.Id == videoSource);
        AudioInputPicker.SelectedItem = AudioInputPicker.Items.OfType<AudioInputSource>()
            .FirstOrDefault(source => source.Id == audioSource);
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
        var format = FormatPicker.SelectedItem as FormatOption;
        var pcm = AudioFormatPicker.SelectedItem as AudioFormatOption;
        FormatExpander.Description = format is not null && pcm is not null ? $"{format.Label} · {pcm.Label}" : "";
        var modeLabel = mode is { Id: null } && observedPreviewMode is { } activeMode
            ? observedPreviewModeIsFallback
                ? LocalFormat("AutoFallbackMode", mode.Label, activeMode.Description)
                : $"{mode.Label} ({activeMode.Description})"
            : mode?.Label;
        var text = device is null || video is null || audio is null || mode is null || format is null || pcm is null
            ? L("Configure")
            : $"{device.Name} · {video.Name} / {audio.Name} · {modeLabel} · " +
                $"{LocalFormat("ShortVideoFormat", DeckLinkCapture.FormatFourCc((uint)format.Id), format.Id == CapturePixelFormat.V210 ? 10 : 8)} / " +
                LocalFormat("ShortPcmFormat", (uint)pcm.Id);
        ConfigurationSummary.Text = text;
        AutomationProperties.SetName(ConfigurationButton, $"{L("Settings")} · {text}");
        ToolTipService.SetToolTip(ConfigurationButton, text);

        var missingSelection = device is not null && ((video is null && settings.DeckLink.VideoSource is not null) ||
            (audio is null && settings.DeckLink.AudioSource is not null) || (mode is null && settings.DeckLink.ModeSelected));
        var message = settingsError is { } error ? error.Message :
            device is null && settings.DeckLink.DeviceName is { } name ? LocalFormat("MissingDevice", name) :
            missingSelection ? L("MissingSelection") : captureTask is not null ? L("ChangeCapture") : "";
        SettingsNotice.Title = settingsError is { } failure ? L(failure.Key) : "";
        SettingsNotice.Message = message;
        SettingsNotice.Severity = settingsError is not null || missingSelection || device is null
            ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        SettingsNotice.IsOpen = message.Length > 0;
        ShowStorageStatus(video, mode, format, pcm);
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
            var result = await Task.Run(() => CaptureSettingsStore.AvailableBytes(directory));
            if (!closing && string.Equals(directory, OutputPath.Text.Trim(), StringComparison.Ordinal))
            {
                checkedFolder = directory;
                availableBytes = result;
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

    private void ShowStorageStatus(VideoInputSource? source, ModeOption? mode, FormatOption? format, AudioFormatOption? pcm)
    {
        var directory = OutputPath.Text.Trim();
        if (directory.Length == 0)
            StorageStatus.Text = L("ChooseStorage");
        else if (!string.Equals(checkedFolder, directory, StringComparison.Ordinal))
            StorageStatus.Text = L("StorageChecking");
        else if (availableBytes is not { } bytes)
            StorageStatus.Text = L("StorageUnavailable");
        else
        {
            var free = LocalFormat("StorageCapacity", (bytes / (double)(1L << 30)).ToString("N1", settingsCulture));
            var selectedMode = format is null ? null : mode?.Id is { } id
                ? source?.ModesFor(format.Id).FirstOrDefault(option => option.Id == id)
                : mode is not null ? observedPreviewMode : null;
            var estimate = selectedMode is null || format is null || pcm is null
                ? L(mode is { Id: null } ? "StorageWaitingForAutoMode" : "StorageNeedsMode") :
                LocalFormat("StorageDuration", FormatRecordingTime(bytes /
                    DeckLinkCapture.EstimateRecordingBytesPerSecond(selectedMode, format.Id, pcm.Id)));
            StorageStatus.Text = $"{free} · {estimate}";
        }
        ToolTipService.SetToolTip(StorageStatus, directory.Length == 0 ? null : directory);
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
}
