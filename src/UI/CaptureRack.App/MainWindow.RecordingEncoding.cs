using System.Globalization;
using CaptureRack.Recording;
using Microsoft.UI.Xaml;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private bool updatingEncoding;

    private bool SupportsSoftwareOutput => IsDeckLinkActive || IsDirectShowActive || IsTestSignalActive;
    private RecordingOutput SelectedRecordingOutput =>
        (RecordingOutputPicker?.SelectedItem as RecordingOutputOption)?.Value ?? RecordingOutput.Native;

    private void BindRecordingOutput()
    {
        var selected = SelectedRecordingOutput;
        var software = L("EncodingSoftware");
        updatingEncoding = true;
        RecordingOutputPicker.Header = L("EncodingOutputLabel");
        RecordingBitrateBox.Header = L("EncodingBitrateLabel");
        RecordingLevelBox.Header = L("EncodingLevelLabel");
        RecordingDeinterlaceBox.Content = L("EncodingDeinterlace");
        RecordingLevelBox.PlaceholderText = L("EncodingAuto");
        RecordingBitrateBox.PlaceholderText = L("EncodingAuto");
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
        var text = RecordingBitrateBox.Text.Trim();
        int? bitrate = null;
        if (text.Length != 0)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 1000000)
                bitrate = parsed;
            else if (validate)
                throw new ArgumentException(L("EncodingBadBitrate"));
        }
        var level = RecordingLevelBox.Text.Trim();
        return new(output, bitrate, level.Length == 0 ? null : level, RecordingDeinterlaceBox.IsChecked == true);
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
        SettingsChanged();
    }

    private void UpdateRecordingEncodingControls()
    {
        if (RecordingEncodingPanel is null || RecordingDeinterlaceBox is null)
            return;
        RecordingEncodingPanel.Visibility = SupportsSoftwareOutput ? Visibility.Visible : Visibility.Collapsed;
        var editable = !IsCaptureRecording && !recordingTransition;
        RecordingOutputPicker.IsEnabled = RecordingBitrateBox.IsEnabled = RecordingLevelBox.IsEnabled =
            RecordingDeinterlaceBox.IsEnabled = editable;
        var selected = SelectedRecordingOutput;
        RecordingEncoderParameters.Visibility = selected is RecordingOutput.Native or RecordingOutput.Ffv1
            ? Visibility.Collapsed : Visibility.Visible;
        RecordingEncodingHint.Text = L(selected switch
        {
            RecordingOutput.Native => "EncodingNativeHint",
            RecordingOutput.Av1 or RecordingOutput.H265 => "EncodingProgressiveHint",
            _ => "EncodingLive"
        });
    }

    public sealed record RecordingOutputOption(RecordingOutput Value, string Label);
}
