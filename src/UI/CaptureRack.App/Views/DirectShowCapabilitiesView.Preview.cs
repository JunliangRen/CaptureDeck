using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private DirectShowPreviewSession? preview;
    private CancellationTokenSource? previewStop;
    private DispatcherQueueTimer? previewTimer;
    private WriteableBitmap? previewBitmap;
    private double leftMeter, rightMeter;
    private bool previewHasSamples;

    internal void StopPreview()
    {
        if (previewStop is null || previewStop.IsCancellationRequested)
            return;
        preview?.SetAudioEnabled(false);
        previewStop.Cancel();
        RenderEvidence();
    }

    private void BindPreview()
    {
        PreviewPlaceholder.Text = L("DsWaitingForFrames");
        MonitorToggle.Header = L("DsListen");
        UpdatePreviewControls();
    }

    private void UpdatePreviewControls()
    {
        var active = previewStop is not null;
        var selected = (VideoFormatPicker.SelectedItem as FormatOption)?.Format;
        var captureOnly = selected?.IsMpeg2Video == true && DirectShowRecording.Supports(selected, null);
        PreviewButton.Content = L(captureOnly
            ? active ? "DsStopInput" : "DsStartInput"
            : active ? "StopPreview" : "StartPreview");
        var supported = selected is not null && (captureOnly || DirectShowPreviewVideo.Encoding(selected) is not null);
        PreviewButton.IsEnabled = active ? !previewStop!.IsCancellationRequested :
            operation is null && capabilities is not null && supported;
        PreviewUnavailable.Visibility = capabilities is not null && selected is not null && !supported
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewUnavailable.Text = L("DsPreviewUnsupported");
        UpdateRecordingControls();
    }

    private async void PreviewClicked(object sender, RoutedEventArgs e)
    {
        if (previewStop is not null)
        {
            StopPreview();
            return;
        }
        if (operation is not null || capabilities?.Device is not { } device ||
            (VideoFormatPicker.SelectedItem as FormatOption)?.Format is not { } video ||
            DirectShowPreviewVideo.Encoding(video) is null && !DirectShowRecording.Supports(video, null))
            return;

        var audio = (AudioPicker.SelectedItem as FormatOption)?.Format;
        using var stop = new CancellationTokenSource();
        previewStop = stop;
        var session = new DirectShowPreviewSession();
        preview = session;
        previewHasSamples = false;
        previewBitmap = null;
        PreviewPicture.Source = null;
        PreviewPlaceholder.Text = L(video.IsMpeg2Video ? "DsMpeg2RecordOnly" : "DsWaitingForFrames");
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible;
        AudioMonitorPanel.Visibility = DirectShowPreviewAudio.Supports(audio) ? Visibility.Visible : Visibility.Collapsed;
        MonitorToggle.IsOn = false;
        leftMeter = rightMeter = 0;
        RenderMeters();
        Notice.IsOpen = false;
        RenderEvidence();

        if (previewTimer is null)
        {
            previewTimer = DispatcherQueue.CreateTimer();
            previewTimer.Interval = TimeSpan.FromMilliseconds(33);
            previewTimer.Tick += (_, _) => RenderPreview();
        }
        previewTimer.Start();
        try
        {
            var report = await session.RunAsync(new(device.Id, video.Index, audio?.Index, TimeSpan.Zero)
            {
                ExpectedVideo = video,
                ExpectedAudio = audio
            }, stop.Token);
            negotiated = report;
            sampled = report;
            if (report.Error is { } error)
                ShowNotice("PreviewFailed", InfoBarSeverity.Error, error);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowNotice("PreviewFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            previewTimer.Stop();
            RenderPreview();
            preview = null;
            previewStop = null;
            MonitorToggle.IsOn = false;
            AudioMonitorPanel.Visibility = Visibility.Collapsed;
            PreviewStatus.Text = L("PreviewStopped");
            RenderEvidence();
        }
    }

    private void RenderPreview()
    {
        if (preview is not { } session)
            return;
        using var frame = session.TakeLatest();
        if (frame is not null)
        {
            if (previewBitmap?.PixelWidth != frame.Width || previewBitmap.PixelHeight != frame.Height)
            {
                previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
                PreviewPicture.Source = previewBitmap;
            }
            using var pixels = previewBitmap.PixelBuffer.AsStream();
            pixels.Position = 0;
            pixels.Write(frame.Bytes);
            previewBitmap.Invalidate();
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }

        if (!previewHasSamples && session.Received > 0)
        {
            previewHasSamples = true;
            UpdateRecordingControls();
        }
        var (left, right) = session.TakePeaks();
        leftMeter = Math.Max(left, leftMeter * 0.90);
        rightMeter = Math.Max(right, rightMeter * 0.90);
        if (AudioMonitorPanel.Visibility == Visibility.Visible)
            RenderMeters();
        PreviewStatus.Text = string.Format(L("DsPreviewFrames"), session.Received, session.Decoded, session.Unreadable);
        if (session.Received > 0 && session.Decoded == 0 && session.Unreadable > 0)
            PreviewPlaceholder.Text = L("DsPreviewUnreadable");
    }

    private void RenderMeters()
    {
        static double Db(double value) => value > 0 ? 20 * Math.Log10(value) : double.NegativeInfinity;
        var left = Db(leftMeter);
        var right = Db(rightMeter);
        PreviewLeftMeter.Value = Math.Clamp(left, -60, 0);
        PreviewRightMeter.Value = Math.Clamp(right, -60, 0);
        AudioLevelText.Text = $"L {left:0.0} / R {right:0.0} dBFS";
    }

    private void MonitorToggled(object sender, RoutedEventArgs e) => preview?.SetAudioEnabled(MonitorToggle.IsOn);
}
