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
    private string activeTheme = "System";

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

        activeModule = moduleRegistry.Resolve(settings.CaptureBackendId);
        var language = SettingsLanguage.Resolve(settings.LanguageOverride, CultureInfo.CurrentUICulture.Name);
        ApplyThemeSelection(settings.Theme);
        workspaceLayout = string.Equals(settings.WorkspaceLayout, "Focus", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceLayoutMode.Focus : WorkspaceLayoutMode.Split;
        splitPreviewRatio = Math.Clamp(settings.SplitPreviewRatio, 0.35, 0.82);
        inspectorPinned = settings.InspectorPinned;
        showSafeArea = settings.ShowSafeArea;
        showNtsc480Guide = settings.ShowNtsc480Guide;
        previewDeinterlace = Enum.TryParse<PreviewDeinterlaceMode>(settings.PreviewDeinterlace, out var deint)
            ? deint : PreviewDeinterlaceMode.BobWeave;
        previewAspectMode = Enum.TryParse<PreviewAspectMode>(settings.PreviewAspectRatio, out var aspect)
            ? aspect : PreviewAspectMode.Auto;
        previewColorMatrix = Enum.TryParse<PreviewColorMatrix>(settings.PreviewColorMatrix, out var matrix)
            ? matrix : PreviewColorMatrix.Auto;
        scopeWaveformMode = string.Equals(settings.ScopeWaveformMode, "RgbParade", StringComparison.OrdinalIgnoreCase)
            ? VideoScopeWaveformMode.RgbParade : VideoScopeWaveformMode.Luma;
        scopeLumaScale = string.Equals(settings.ScopeLumaScale, "Ire", StringComparison.OrdinalIgnoreCase)
            ? ScopeLumaScaleMode.Ire : ScopeLumaScaleMode.Code;
        audioMonitorEnabled = settings.AudioMonitorEnabled;
        audioMonitorVolume = Math.Clamp(settings.AudioMonitorVolume, 0.0, 1.0);
        deckLinkInspector.RestoreAnalogSettings(
            settings.DeckLink.AnalogNtscIre,
            settings.DeckLink.AnalogComponentLevel,
            settings.DeckLink.AnalogAudioLevel,
            settings.DeckLink.AnalogAudioGainOverride,
            settings.DeckLink.AnalogAudioGainLeftDb,
            settings.DeckLink.AnalogAudioGainRightDb,
            settings.DeckLink.AnalogAudioGainLinked);

        ApplySettingsLanguage(language, settings.DeckLink.VideoFormat, settings.DeckLink.AudioFormat);
        OutputPath.Text = settings.OutputFolder ?? "";
        RecordingLabelBox.Text = settings.RecordingLabel ?? "";
        UpdateRecordingFilenamePreview();
        if (loaded && settings.LanguageOverride != language)
            SaveSettings(settings with { LanguageOverride = language });
        settingsReady = true;
        loading = false;
    }

    private void ApplyThemeSelection(string? theme)
    {
        activeTheme = theme is "Dark" or "Light" or "System" ? theme : "System";
        RootShell.RequestedTheme = activeTheme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        SyncSettingsPageSelections();
        UpdateTitleBarTheme();
    }

    private void SyncSettingsPageSelections()
    {
        var layoutTag = workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split";
        SettingsPage.SyncSelections(
            activeTheme,
            settingsCulture.Name,
            layoutTag,
            previewDeinterlace.ToString(),
            previewAspectMode.ToString(),
            previewColorMatrix.ToString());
    }

    private void OnThemeSelectionChanged(string theme)
    {
        if (!settingsReady || loading)
            return;

        ApplyThemeSelection(theme);
        RefreshDynamicThemeBrushes();
        SettingsChanged();
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
        if (deckLinkInspector.SelectedDevice is { } device)
        {
            var sameDevice = deckLink.DeviceId is { } id ? device.PersistentId == id : deckLink.DeviceName == device.Name;
            var video = deckLinkInspector.SelectedVideoSource;
            var audio = deckLinkInspector.SelectedAudioSource;
            var mode = deckLinkInspector.SelectedMode;
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
            VideoFormat = (uint)deckLinkInspector.SelectedVideoFormat,
            AudioFormat = (uint)deckLinkInspector.SelectedAudioFormat,
            AnalogNtscIre = deckLinkInspector.AnalogNtscIre,
            AnalogComponentLevel = deckLinkInspector.AnalogComponentLevel,
            AnalogAudioLevel = deckLinkInspector.AnalogAudioLevel,
            AnalogAudioGainOverride = deckLinkInspector.AnalogAudioGainOverride,
            AnalogAudioGainLeftDb = deckLinkInspector.AnalogAudioGainLeftDb,
            AnalogAudioGainRightDb = deckLinkInspector.AnalogAudioGainRightDb,
            AnalogAudioGainLinked = deckLinkInspector.AnalogAudioGainLinked
        };
        var savedLayout = workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split";
        SaveSettings(settings with
        {
            CaptureBackendId = activeModule.Descriptor.BackendId,
            DeckLink = deckLink,
            OutputFolder = OutputPath.Text.Trim(),
            RecordingLabel = RecordingLabelBox.Text.Trim(),
            LanguageOverride = settingsCulture.Name,
            Theme = activeTheme,
            WorkspaceLayout = savedLayout,
            SplitPreviewRatio = Math.Round(splitPreviewRatio, 3),
            InspectorPinned = inspectorPinned,
            ShowSafeArea = showSafeArea,
            ShowNtsc480Guide = showNtsc480Guide,
            PreviewDeinterlace = previewDeinterlace.ToString(),
            PreviewAspectRatio = previewAspectMode.ToString(),
            PreviewColorMatrix = previewColorMatrix.ToString(),
            ScopeWaveformMode = scopeWaveformMode.ToString(),
            ScopeLumaScale = scopeLumaScale.ToString(),
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

    private void RefreshToolchainDiagnostics()
    {
        var (ffmpeg, ffprobe) = QcReports.FindTools();
        var ready = ffmpeg is not null && ffprobe is not null;
        SettingsPage.ShowToolchainStatus(
            ready,
            L(ready ? "ToolchainReady" : "ToolchainMissing"),
            ready ? Path.GetDirectoryName(ffmpeg) ?? L("QcToolsDetail") : L("QcToolsDetail"));
    }

    private async Task OpenSettingsFolderAsync()
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

    private void ApplySettingsLanguage(string language, uint? videoFormat, uint? audioFormat, bool preserveActiveRouting = false)
    {
        settingsCulture = CultureInfo.GetCultureInfo(language);
        AutomationProperties.SetName(OpenSettingsButton, L("Settings"));
        ToolTipService.SetToolTip(OpenSettingsButton, L("Settings"));

        InputHeading.Text = L("Capture");
        OutputHeading.Text = L("Output");
        OutputHelpText.Text = L("OutputHelp");
        AutomationProperties.SetName(OutputPath, L("Folder"));
        OutputPath.PlaceholderText = L("ChooseFolder");
        RecordingLabelHeading.Text = L("RecordingLabelHeading");
        RecordingLabelBox.PlaceholderText = L("RecordingLabelPlaceholder");
        BrowseLabel.Text = L("Browse");

        deckLinkInspector.SetFormats(videoFormat, audioFormat);
        deckLinkInspector.ApplyLanguage(L, settingsCulture, preserveActiveRouting);
        UpdateRecordingFilenamePreview();

        ApplyTestSignalLanguage();
        if (!HasLiveCapture)
        {
            plannedStageView.Bind(activeModule.Descriptor, L);
            plannedInspectorView.Bind(activeModule.Descriptor, L);
        }

        var layoutTag = workspaceLayout == WorkspaceLayoutMode.Focus ? "Focus" : "Split";
        SettingsPage.ApplyLanguage(
            L,
            activeTheme,
            language,
            layoutTag,
            previewDeinterlace.ToString(),
            previewAspectMode.ToString(),
            previewColorMatrix.ToString());

        ApplySignalAndMonitorLanguage();
        ApplyWorkspaceLanguage();
        RefreshToolchainDiagnostics();
    }

    private void OnLanguageSelectionChanged(string language)
    {
        if (!settingsReady || loading)
            return;

        loading = true;
        ApplySettingsLanguage(
            language,
            (uint)deckLinkInspector.SelectedVideoFormat,
            (uint)deckLinkInspector.SelectedAudioFormat,
            preserveActiveRouting: true);
        loading = false;
        UpdateControls();
        SettingsChanged();
    }

    private void ShowSettingsError(string key, string message)
    {
        settingsError = (key, message);
        UpdateConfigurationStatus();
    }

    private void UpdateConfigurationStatus()
    {
        if (!IsDeckLinkActive)
        {
            var backendName = L(activeModule.Descriptor.DisplayNameKey);
            var text = IsTestSignalActive
                ? $"{backendName} · {L(TestSignalScenarioKey)} · {TestSignal.Mode.Description} · " +
                    $"{LocalFormat("ShortVideoFormat", "v210", 10)} / {LocalFormat("ShortPcmFormat", (uint)TestSignal.AudioFormat)}"
                : $"{backendName} · {L(IsDirectShowActive ? "DsDiagnostics" : "SourceStatusPlanned")}";
            ConfigurationSummary.Text = text;
            AutomationProperties.SetName(QuickSourceButton, $"{L("Capture")} · {text}");
            ToolTipService.SetToolTip(QuickSourceButton, text);
            QuickSourceDot.Fill = ThemeBrush("StudioInfoBrush");
            InspectorNotice.Visibility = settingsError is not null ? Visibility.Visible : Visibility.Collapsed;
            if (settingsError is { } err)
            {
                InspectorNoticeText.Text = err.Message;
                InspectorNotice.Background = ThemeBrush("StudioCautionSoftBrush");
                InspectorNotice.BorderBrush = ThemeBrush("StudioCautionBrush");
                InspectorNoticeIcon.Foreground = ThemeBrush("StudioCautionBrush");
                InspectorNoticeIcon.Glyph = "\uE7BA";
            }
            SettingsPage.ShowNotice(
                settingsError is { } fail ? L(fail.Key) : "",
                settingsError?.Message ?? "",
                InfoBarSeverity.Warning,
                settingsError is not null);
            ShowStorageStatus(IsTestSignalActive ? TestSignal.Mode : null, null);
            return;
        }

        var device = deckLinkInspector.SelectedDevice;
        var video = deckLinkInspector.SelectedVideoSource;
        var audio = deckLinkInspector.SelectedAudioSource;
        var mode = deckLinkInspector.SelectedMode;
        var modeLabel = mode is { Id: null } && observedPreviewMode is { } activeMode
            ? observedPreviewModeIsFallback
                ? LocalFormat("AutoFallbackMode", mode.Label, activeMode.Description)
                : $"{mode.Label} ({activeMode.Description})"
            : mode?.Label;
        var summary = device is null || video is null || audio is null || mode is null
            ? L("Configure")
            : $"{device.Name} · {video.Name} / {audio.Name} · {modeLabel} · " +
                $"{LocalFormat("ShortVideoFormat", DeckLinkCapture.FormatFourCc((uint)deckLinkInspector.SelectedVideoFormat), deckLinkInspector.SelectedVideoFormat == CapturePixelFormat.V210 ? 10 : 8)} / " +
                LocalFormat("ShortPcmFormat", (uint)deckLinkInspector.SelectedAudioFormat);
        ConfigurationSummary.Text = summary;
        AutomationProperties.SetName(QuickSourceButton, $"{L("Capture")} · {summary}");
        ToolTipService.SetToolTip(QuickSourceButton, summary);

        var selectedMode = mode?.Id is { } id
            ? video?.ModesFor(deckLinkInspector.SelectedVideoFormat).FirstOrDefault(option => option.Id == id)
            : mode is not null ? observedPreviewMode : null;
        deckLinkInspector.UpdateBitrateBadge(selectedMode);

        var missingSelection = device is not null && ((video is null && settings.DeckLink.VideoSource is not null) ||
            (audio is null && settings.DeckLink.AudioSource is not null) || (mode is null && settings.DeckLink.ModeSelected));
        var message = settingsError is { } error ? error.Message :
            device is null && settings.DeckLink.DeviceName is { } name ? LocalFormat("MissingDevice", name) :
            missingSelection ? L("MissingSelection") : captureTask is not null ? L("ChangeCapture") : "";
        var isWarning = settingsError is not null || missingSelection || device is null;

        SettingsPage.ShowNotice(
            settingsError is { } failure ? L(failure.Key) : "",
            message,
            isWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            settingsError is not null);

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

    private void ShowStorageStatus(VideoMode? selectedMode, Views.DeckLinkModeOption? mode)
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
            var estimate = !HasLiveCapture
                ? free
                : selectedMode is null
                    ? L(mode is { Id: null } ? "StorageWaitingForAutoMode" : "StorageNeedsMode")
                    : LocalFormat("StorageDuration", FormatRecordingTime(space.AvailableBytes /
                        DeckLinkCapture.EstimateRecordingBytesPerSecond(selectedMode, RecordingVideoFormat, RecordingAudioFormat)));
            InspectorStorageStatus.Text = HasLiveCapture ? $"{free} · {estimate}" : free;
            StorageStatus.Text = HasLiveCapture && selectedMode is not null ? estimate : free;

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
        SettingsPage.ShowAboutDescription($"Windows {arch} · {sdk}");
    }
}
