using System.Globalization;
using CaptureRack.App.Controls;
using CaptureRack.DeckLink;
using CaptureRack.Recording;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private bool updatingEncoding;
    private bool encoderParamsWereVisible;

    private bool SupportsSoftwareOutput => IsDeckLinkActive || IsDirectShowActive || IsTestSignalActive;
    private RecordingOutput SelectedRecordingOutput =>
        (RecordingOutputPicker?.SelectedItem as RecordingOutputOption)?.Value ?? RecordingOutput.Native;

    private bool HasInvalidSoftwareBitrate =>
        SupportsSoftwareOutput &&
        SelectedRecordingOutput is not (RecordingOutput.Native or RecordingOutput.Ffv1) &&
        !EncodingUxHelper.ValidateBitrateText(RecordingBitrateBox?.Text).IsValid;

    private void BindRecordingOutput()
    {
        var selected = SelectedRecordingOutput;
        var software = L("EncodingSoftware");
        updatingEncoding = true;
        RecordingOutputLabel.Text = L("EncodingOutputLabel");
        RecordingBitrateLabel.Text = L("EncodingBitrateLabel");
        RecordingLevelLabel.Text = L("EncodingLevelLabel");
        AutomationProperties.SetName(RecordingOutputPicker, L("EncodingOutputLabel"));
        AutomationProperties.SetName(RecordingBitrateBox, L("EncodingBitrateLabel"));
        AutomationProperties.SetName(RecordingLevelBox, L("EncodingLevelLabel"));
        RecordingDeinterlaceBox.Content = L("EncodingDeinterlace");
        RecordingLevelBox.PlaceholderText = L("EncodingAuto");
        RecordingBitrateBox.PlaceholderText = L("EncodingAuto");
        BitratePresetAutoBtn.Content = L("EncodingPresetAuto");
        RecordingBitrateErrorText.Text = L("EncodingBadBitrate");
        RecordingInterlacedWarningText.Text = L("EncodingInterlacedWarning");
        EnableDeinterlaceQuickButton.Content = L("EncodingEnableDeinterlace");
        var options = new RecordingOutputOption[]
        {
            new(RecordingOutput.Native, L("EncodingNative")),
            new(RecordingOutput.Ffv1, $"{software} · FFV1 + FLAC 8"),
            new(RecordingOutput.H264, $"{software} · H.264"),
            new(RecordingOutput.H265, $"{software} · H.265"),
            new(RecordingOutput.Av1, $"{software} · AV1"),
            new(RecordingOutput.Mpeg2, $"{software} · MPEG-2")
        };
        RecordingOutputPicker.ItemsSource = options;
        RecordingOutputPicker.SelectedItem = options.FirstOrDefault(option => option.Value == selected) ?? options[0];
        updatingEncoding = false;
        UpdateRecordingEncodingControls();
    }

    private void RestoreRecordingEncoding(RecordingEncoding? encoding)
    {
        updatingEncoding = true;
        if (RecordingOutputPicker.ItemsSource is RecordingOutputOption[] options)
            RecordingOutputPicker.SelectedItem = options.FirstOrDefault(option => option.Value == encoding?.Output) ?? options[0];
        RecordingBitrateBox.Text = encoding?.VideoKbps?.ToString(CultureInfo.InvariantCulture) ?? "";
        RecordingLevelBox.Text = encoding?.Level ?? "";
        RecordingDeinterlaceBox.IsChecked = encoding?.Deinterlace == true;
        updatingEncoding = false;
        UpdateRecordingEncodingControls();
    }

    private RecordingEncoding ReadRecordingEncoding(bool validate = true)
    {
        var output = SelectedRecordingOutput;
        if (output is RecordingOutput.Native or RecordingOutput.Ffv1)
            return new(output);
        var (isValid, parsed) = EncodingUxHelper.ValidateBitrateText(RecordingBitrateBox.Text);
        if (!isValid && validate)
            throw new ArgumentException(L("EncodingBadBitrate"));
        var level = RecordingLevelBox.Text.Trim();
        return new(output, parsed, level.Length == 0 ? null : level, RecordingDeinterlaceBox.IsChecked == true);
    }

    private void RecordingOutputChanged(object sender, RoutedEventArgs e)
    {
        if (updatingEncoding || RecordingEncodingPanel is null)
            return;
        if (ReferenceEquals(sender, RecordingOutputPicker))
        {
            updatingEncoding = true;
            RecordingLevelBox.Text = "";
            updatingEncoding = false;
        }
        UpdateRecordingFilenamePreview();
        UpdateConfigurationStatus();
        UpdateControls();
        SettingsChanged();
    }

    private void BitratePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset } || !RecordingBitrateBox.IsEnabled)
            return;
        RecordingBitrateBox.Text = preset;
    }

    private void EnableDeinterlaceQuick_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingDeinterlaceBox.IsEnabled)
            RecordingDeinterlaceBox.IsChecked = true;
    }

    private bool? CurrentSourceIsInterlaced()
    {
        if (previewFrame is { } frame)
            return frame.IsInterlaced;
        if (IsTestSignalActive)
            return TestSignal.Mode.IsInterlaced;
        if (IsDeckLinkActive)
        {
            if (observedPreviewMode is { } observed)
                return observed.IsInterlaced;
            var mode = deckLinkInspector.SelectedMode;
            if (mode?.Id is { } id && deckLinkInspector.SelectedVideoSource is { } src)
                return src.ModesFor(deckLinkInspector.SelectedVideoFormat).FirstOrDefault(m => m.Id == id)?.IsInterlaced;
        }
        if (LiveMediaSource is { } media)
            return media.HasDeinterlace ? true : null;
        return null;
    }

    private void UpdateRecordingEncodingControls()
    {
        if (RecordingEncodingPanel is null || RecordingDeinterlaceBox is null)
            return;
        RecordingEncodingPanel.Visibility = SupportsSoftwareOutput ? Visibility.Visible : Visibility.Collapsed;
        var editable = !IsCaptureRecording && !recordingTransition && !(IsRfActive && rfInspector.IsPlayback);
        RecordingOutputPicker.IsEnabled = RecordingBitrateBox.IsEnabled = RecordingLevelBox.IsEnabled =
            RecordingDeinterlaceBox.IsEnabled = BitratePresetAutoBtn.IsEnabled =
            BitratePreset15Btn.IsEnabled = BitratePreset25Btn.IsEnabled = BitratePreset50Btn.IsEnabled = editable;
        var selected = SelectedRecordingOutput;
        var showParams = selected is not (RecordingOutput.Native or RecordingOutput.Ffv1);
        RecordingEncoderParameters.Visibility = showParams ? Visibility.Visible : Visibility.Collapsed;
        if (showParams && !encoderParamsWereVisible && settingsReady)
            StudioFeedback.RevealSubtle(RecordingEncoderParameters, offsetY: 4f, durationMs: 130);
        encoderParamsWereVisible = showParams;

        // Live inline bitrate validation
        var (bitrateValid, _) = EncodingUxHelper.ValidateBitrateText(RecordingBitrateBox.Text);
        RecordingBitrateErrorBanner.Visibility = showParams && !bitrateValid ? Visibility.Visible : Visibility.Collapsed;

        // Interlaced warning + one-click software deinterlace helper for H.265 / AV1
        var warnInterlaced = showParams && EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            selected, CurrentSourceIsInterlaced(), RecordingDeinterlaceBox.IsChecked == true);
        RecordingInterlacedBanner.Visibility = warnInterlaced ? Visibility.Visible : Visibility.Collapsed;
        EnableDeinterlaceQuickButton.IsEnabled = editable;

        RecordingEncodingHint.Text = L(selected switch
        {
            RecordingOutput.Native => "EncodingNativeHint",
            RecordingOutput.Av1 or RecordingOutput.H265 => "EncodingProgressiveHint",
            _ => "EncodingLive"
        });
    }

    public sealed record RecordingOutputOption(RecordingOutput Value, string Label);
}
