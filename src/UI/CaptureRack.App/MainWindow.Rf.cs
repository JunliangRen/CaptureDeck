#if REALTIME_RF
using VHSDecode.Preview;
#endif
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using CaptureRack.SignalCapture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private readonly Views.RfInspectorView rfInspector = new();
    private bool rfFileSound = true;
    private bool IsRfHifiFile => IsRfActive && rfInspector.PreviewOptions.PreviewType == RfPreviewType.VhsHifi;
    private bool RfHasAudio => IsRfActive && rfInspector.PreviewOptions.PreviewType is RfPreviewType.VhsHifi or RfPreviewType.BetamaxHifi or RfPreviewType.LaserDisc;
    private bool IsRfActive => activeModule.Descriptor.Category == CaptureBackendCategory.SignalCapture;
    private CancellationTokenSource? rfStop;
    private Task? rfTask;
    private bool rfWriterActive;
    private RfMeasurement? latestRfMeasurement;
    private RfMeasurement? displayedRfMeasurement;
    private RfRecording? measuredSource;

    private CancellationTokenSource? rfPreviewStop;
    private Task? rfPreviewTask;
    private RfPreviewSession? rfPreview;
    private bool rfPreviewReady;

    private RfPreviewAudio? rfAudio;
    private RfPreviewType? meterPreviewType;
    private long displayedAudioGeneration = -1;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer rfAudioTimer;

    private void UpdateAudioMeterVisibility(double width)
    {
        bool enabled = IsRfActive ? rfAudio is not null : HasLiveCapture && ActiveWorkspaceCapabilities.SupportsAudioMeters && width >= 680;
        StereoMeter.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureRfAudioMeter(bool newSession = false)
    {
        RfPreviewType? type = IsRfActive ? rfInspector.PreviewOptions.PreviewType : null;
        if (newSession || type != meterPreviewType)
        {
            meterPreviewType = type;
            bool supported = type is RfPreviewType.VhsHifi or RfPreviewType.BetamaxHifi or RfPreviewType.LaserDisc;
            rfAudio = supported ? new RfPreviewAudio(rfInspector.PreviewOptions.AudioTrackLabel) : null;
            displayedAudioGeneration = -1;
            StereoMeter.Reset();
            if (IsRfActive)
            {
                monitorOptionsBar.ApplyCapabilities(ActiveWorkspaceCapabilities, !IsRfHifiFile, false, !IsRfHifiFile);
                ApplyPreviewOptionsToUi();
            }
            if (supported) rfAudioTimer.Start();
            else rfAudioTimer.Stop();
        }
        if (rfAudio is null) StereoMeter.SetPreviewTrackStatus(null);
        else RenderRfAudio();
        UpdateAudioMeterVisibility(lastWindowWidth);
        UpdateRfSpectrumSource();
    }

    private void RenderRfAudio()
    {
        if (rfAudio is null) return;
        bool spectrum = ShowRfPcmSpectrum && InstrumentsHost.Visibility == Visibility.Visible && SpectrumBody.Visibility == Visibility.Visible;
        var audio = rfAudio.Read(spectrum);
        if (audio.Generation != displayedAudioGeneration)
        {
            StereoMeter.Reset();
            displayedAudioGeneration = audio.Generation;
            if (spectrum) ResetRfSpectrum();
        }
        string? status = audio.Locked ? audio.Track : string.IsNullOrEmpty(audio.Track)
            ? L("RfWaitingAudio") : $"{audio.Track} · {L("RfWaitingAudio")}";
        StereoMeter.SetPreviewTrackStatus(status, waiting: !audio.Locked);
        if (audio.Locked && audio.Levels is { } levels) StereoMeter.UpdateLevels(levels, settingsCulture);
        if (spectrum && audio.Levels is { Spectrum: { } bins } pcm)
        {
            if (SpectrumLevelAxis.Children.Count == 0) RenderSpectrumLabels(pcm);
            SecondaryInstrumentSubtitle.Text = audio.Track ?? "PCM";
            PaintSpectrum(bins, pcm.SpectrumFloorDbfs);
        }
        StereoMeter.StepBallistics();
    }

    private void ClearRfVideoScopes()
    {
        visibleScopes = null;
        WaveformPlot.Source = VectorPlot.Source = null;
        RenderScopeScaleAxis(null);
        RenderLumaRange(null);
    }

    private void RfFieldStep_Click(object sender, RoutedEventArgs args)
    {
#if REALTIME_RF
        StepRfFieldAsync(sender);
#endif
    }

#if REALTIME_RF
    private async void StepRfFieldAsync(object sender)
    {
        if (inspectingField || rfPreviewStop is not null || heldDecoder is not { } renderer ||
            heldPair is not { } pair || rfInspector.PlaybackFile is not { } file || sender is not Button button) return;
        bool forward = button.Tag as string == "1";
        int mode = rfInspector.PreviewOptions.FieldMode;
        var current = mode == 2 ? pair.Top : mode == 3 ? pair.Bottom : pair.First;
        PreviewField? selected;
        if (forward && ReferenceEquals(current, pair.First)) selected = pair.Second;
        else if (!forward && ReferenceEquals(current, pair.Second)) selected = pair.First;
        else
        {
            inspectingField = true;
            UpdateControls();
            try
            {
                var ffmpeg = file.ContainerRate > 0 ? QcReports.FindTools().FfmpegPath : null;
                var found = await Task.Run(() => FindAdjacentPair(file, renderer.Options, current, forward, ffmpeg));
                if (found is null || heldDecoder != renderer || rfPreviewStop is not null || closing) return;
                pair = heldPair = found;
                selected = forward ? pair.First : pair.Second;
            }
            catch (Exception ex) { SetStatus("PreviewFailed", () => ex.Message, InfoBarSeverity.Error); return; }
            finally { inspectingField = false; UpdateControls(); }
        }
        playbackPosition = Math.Clamp((long)Math.Round(selected.Timestamp.TotalSeconds * file.Rate), 0, file.Samples);
        rfInspector.PreviewOptions.FieldMode = selected.IsTopField ? 2 : 3;
        RefreshRfInspection();
        UpdateRfPlaybackBar(true);
    }

    private static RfPreviewDecoder.FieldPair? FindAdjacentPair(RfPlaybackFile file, RealtimePreviewOptions options,
        PreviewField current, bool forward, string? ffmpeg)
    {
        // Start before the target so sync, the colour PLL and the resampler can settle.
        double t = current.Timestamp.TotalSeconds, period = 1 / current.FieldsPerSecond;
        long start = Math.Max(0, (long)((t - 0.6) * file.Rate));
        using var stop = new CancellationTokenSource();
        PreviewField? previous = null;
        RfPreviewDecoder.FieldPair? best = null;
        var trigger = options.SourceFormat is PreviewSourceFormat.Cvbs or PreviewSourceFormat.LaserDisc ? null : new RfSwitchTrigger();
        using var session = new RealtimePreviewSession(options with { AudioTrack = PreviewAudioTrack.None }, field =>
        {
            if (previous is { } first && first.IsTopField != field.IsTopField &&
                Math.Abs((field.Timestamp-first.Timestamp).TotalSeconds / period - 1) < 0.45)
            {
                if (forward && first.Timestamp.TotalSeconds > t + period*0.5 ||
                    !forward && field.Timestamp.TotalSeconds < t - period*0.5)
                    best = new(first, field, trigger?.Find(first.Timestamp, file.Rate));
            }
            previous = field;
            if (forward && best is not null || !forward && field.Timestamp.TotalSeconds >= t) stop.Cancel();
        });
        try
        {
            file.Play(start, ffmpeg, _ => { }, (bytes, length, first) =>
            {
                if (first > (t + 0.6) * file.Rate) { stop.Cancel(); return; }
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, length));
                trigger?.Add(samples, first);
                session.Process(samples, first, stop.Token);
            }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return best;
    }
#endif

    private void UpdateFieldButtons()
    {
#if REALTIME_RF
        bool held = heldDecoder is not null && heldPair is not null && rfPreviewStop is null && rfInspector.IsPlayback;
#else
        bool held = false;
#endif
        RfStepBack.Visibility = RfStepForward.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        RfStepBack.IsEnabled = RfStepForward.IsEnabled = held && !inspectingField;
    }

    private void DiscardRfInspection()
    {
#if REALTIME_RF
        bool hadPicture = heldPair is not null;
        heldPair = null;
        heldDecoder = null;
        if (hadPicture) { ResetPreview(null); ClearRfVideoScopes(); }
#endif
        if (RfScopeValue is null) return;
        RfScopeValue.Text = "";
        if (RfScopeSource.SelectedIndex > 0) RfTraceA.Points.Clear();
        UpdateFieldButtons();
    }

    private void RefreshRfInspection()
    {
#if REALTIME_RF
        if (heldDecoder is not { } renderer || heldPair is not { } pair || rfPreviewStop is not null) return;
        renderer.FieldMode = rfInspector.PreviewOptions.FieldMode;
        renderer.FullRaster = rfInspector.PreviewOptions.FullRaster;
        renderer.ScopeFeed.ScopeWaveformMode = scopeWaveformMode;
        renderer.ScopeFeed.ScopeIreScale = scopeLumaScale == ScopeLumaScaleMode.Ire;
        renderer.ScopeFeed.DeinterlaceMode = previewDeinterlace;
        renderer.ScopeFeed.RenderScopes = true;
        previewFrame = null;
        renderer.RenderPair(pair);
        rfGuide = renderer.NtscGuide is { } guide ? (guide.Top, guide.Bottom) : null;
        UpdateNtsc480GuideLayout();
        PresentPreview(renderer.ScopeFeed);
        renderer.ScopeFeed.VisitScopes(PaintVideoScopes);
        PaintSignalScope(pair, force: true);
#endif
    }

    private void RfScope_Changed(object sender, SelectionChangedEventArgs args) => RefreshSignalScope();

    private void RefreshSignalScope()
    {
        if (RfScopeValue is null) return;
        int mode = RfScopeSource.SelectedIndex;
        RfTraceA.Points.Clear();
        RfTraceB.Points.Clear();
        RfScopeValue.Text = "";
#if REALTIME_RF
        if (heldPair is { } pair) PaintSignalScope(pair, force: true);
#endif
        if (mode == 0 && displayedRfMeasurement is { } measurement)
        {
            RfTraceA.Points = RfAdcTrace(measurement.A, measurement.FullScale);
            if (measurement.B is { } b) RfTraceB.Points = RfAdcTrace(b, measurement.FullScale);
        }
    }

#if REALTIME_RF
    private long lastSignalPaint;

    private void PaintSignalScope(RfPreviewDecoder.FieldPair pair, bool force = false)
    {
        int mode = RfScopeSource.SelectedIndex;
        if (mode <= 0 || !force && (RfStage.Visibility != Visibility.Visible || Environment.TickCount64 - lastSignalPaint < 70)) return;
        lastSignalPaint = Environment.TickCount64;
        var points = new PointCollection();
        if (pair.SwitchTrace is not { } trace) { RfTraceA.Points.Clear(); RfScopeValue.Text = "—"; return; }
        for (int i = 0; i < trace.Length; i++) points.Add(new Point(i, 50 - trace[i] * 49.0 / 32768));
        RfScopeValue.Text = "↥";
        RfTraceA.Points = points;
    }
#endif

    private static PointCollection RfAdcTrace(RfLevels levels, int fullScale)
    {
        var points = new PointCollection();
        for (int i = 0; i < levels.Trace.Length; i++) points.Add(new Point(i, 50 - 49d * levels.Trace[i] / fullScale));
        return points;
    }

    private void ResetRfMonitor()
    {
        latestRfMeasurement = displayedRfMeasurement = null;
        ResetRfSpectrum();
        RfTraceA.Points.Clear();
        RfTraceB.Points.Clear();
        RfMeterA.Reset();
        RfMeterB.Reset();
        RfMeterB.Visibility = (rfInspector.IsPlayback ? rfInspector.PlaybackFile?.IsIq == true : rfInspector.Selection?.Rtl is not null) ? Visibility.Visible : Visibility.Collapsed;
        RfReading.Text = RfFile.Text = "";
        rfInspector.SetTelemetry("");
    }

    private (double Top, double Bottom)? rfGuide;

#if REALTIME_RF
    private RfPreviewDecoder? heldDecoder;
    private RfPreviewDecoder.FieldPair? heldPair;
    private bool inspectingField;
#else
    private const bool inspectingField = false;
#endif
    private long playbackPosition;
    private bool updatingSeek;
    private bool openingPlayback;
    private bool rfWasPlayback;
    private CancellationTokenSource? playbackSegment;
    private int seekVersion;

    private void UpdateRfPlaybackBar(bool visible)
    {
        RfPlaybackBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var file = rfInspector.PlaybackFile;
        RfOpenFile.Content = L("RfOpenFile");
        RfOpenFile.IsEnabled = !inspectingField && !openingPlayback && rfPreviewStop is null;
        RfSeek.IsEnabled = file is not null && !openingPlayback && !inspectingField;
        UpdateFieldButtons();
        RfPlaybackName.Text = file is null ? "" : Path.GetFileName(file.Path);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RfSeek, L("RfPlayback"));
        updatingSeek = true;
        try
        {
            RfSeek.Maximum = file is null ? 1 : (double)file.Samples / file.Rate;
            RfSeek.Value = file is null ? 0 : (double)playbackPosition / file.Rate;
        }
        finally { updatingSeek = false; }
        RfPlaybackTime.Text = file is null ? "" : $"{PlaybackTime(RfSeek.Value)} / {PlaybackTime(RfSeek.Maximum)}";
    }

    private static string PlaybackTime(double seconds) =>
        $"{(long)seconds / 3600:00}:{(int)(seconds / 60 % 60):00}:{(int)seconds % 60:00}.{(long)(seconds * 10) % 10}";

    private async void RfOpenFile_Click(object sender, RoutedEventArgs args)
    {
        if (openingPlayback || rfPreviewStop is not null || !rfInspector.IsPlayback) return;
        openingPlayback = true;
        UpdateControls();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id) { Title = L("RfOpenFile") };
            foreach (string extension in RfPlaybackFile.Extensions) picker.FileTypeFilter.Add(extension);
            var picked = await picker.PickSingleFileAsync();
            if (picked is null) return;
            await LoadRfPlaybackAsync(picked.Path);
        }
        catch (Exception ex) { SetStatus("PreviewFailed", () => ex.Message, InfoBarSeverity.Error); }
        finally { openingPlayback = false; UpdateControls(); }
    }

    private async Task LoadRfPlaybackAsync(string path)
    {
        var probe = Path.GetExtension(path).ToLowerInvariant() is ".flac" or ".ldf" ? QcReports.FindTools().FfprobePath : null;
        var file = await Task.Run(() => RfPlaybackFile.Open(path, probe));
        if (closing || !IsRfActive || !rfInspector.IsPlayback) return;
        playbackPosition = 0;
        DiscardRfInspection();
        ResetRfMonitor();
        rfInspector.SetPlaybackFile(file);
        RfFile.Text = file.Path;
    }

    private void RfSeek_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (updatingSeek || !rfInspector.IsPlayback || rfInspector.PlaybackFile is not { } file) return;
        playbackPosition = Math.Clamp((long)Math.Round(args.NewValue * file.Rate), 0, file.Samples);
        DiscardRfInspection();
        seekVersion++;
        if (RfHasAudio && activeAudioMonitor is { } speaker) speaker.Enabled = false;
        playbackSegment?.Cancel();
        RfPlaybackTime.Text = $"{PlaybackTime(args.NewValue)} / {PlaybackTime(RfSeek.Maximum)}";
    }

    private async Task PreviewRfAsync(RfRecording? request, RfPlaybackFile? file = null)
    {
        using var stop = new CancellationTokenSource();
        rfPreviewStop = stop;
        var session = rfPreview = request is null ? null : new RfPreviewSession(request);
        if (file is not null && playbackPosition >= file.Samples) playbackPosition = 0;
        int activeSeek = seekVersion;
        var vga = rfInspector.VgaReader();
        var timer = DispatcherQueue.CreateTimer();
        Action<byte[], int, long>? offer = null;
#if REALTIME_RF
        RfPreviewDecoder? decoder = null;
        Action? resetDecoder = null;
        Task retiring = Task.CompletedTask;
        bool reachedEnd = false;
        long presented = 0;
        long lastField = 0;
        string? decoderError = null;
#endif
        timer.Interval = TimeSpan.FromMilliseconds(rfInspector.PreviewOptions.PreviewType == RfPreviewType.RfOnly ? 100 : 8);
        timer.Tick += (_, _) =>
        {
#if REALTIME_RF
            if (decoder is not null)
            {
                var feed = decoder.ScopeFeed;
                decoder.FullRaster = rfInspector.PreviewOptions.FullRaster;
                decoder.FieldMode = rfInspector.PreviewOptions.FieldMode;
                decoder.MeasureSwitch = rfInspector.IsPlayback || RfScopeSource.SelectedIndex == 1;
                feed.DeinterlaceMode = previewDeinterlace;
                feed.ScopeWaveformMode = scopeWaveformMode;
                feed.ScopeIreScale = scopeLumaScale == ScopeLumaScaleMode.Ire;
                feed.RenderScopes = !PauseMainWindowRendering && EffectiveWorkspaceLayout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes;
                var crop = decoder.NtscGuide;
                (double Top, double Bottom)? guide = crop is null ? null : (crop.Top, crop.Bottom);
                if (rfGuide != guide)
                {
                    rfGuide = guide;
                    UpdateNtsc480GuideLayout();
                    SyncFullscreenPreviewWindow();
                }
                PresentPreview(feed);
                if (feed.PresentedSequence != presented)
                {
                    presented = feed.PresentedSequence;
                    if (feed.RenderScopes) feed.VisitScopes(PaintVideoScopes);
                    if (previewFrame?.SourceTag is RfPreviewDecoder.FieldPair pair)
                    {
                        heldPair = pair;
                        PaintSignalScope(pair);
                    }
                    lastField = Environment.TickCount64;
                }
                else if (previewBitmap is not null && Environment.TickCount64 - lastField > 1000)
                {
                    DiscardRfInspection();
                    ResetPreview(null);
                    ClearRfVideoScopes();
                }
            }
            if (decoder?.Error is { } failure && failure != decoderError)
            {
                decoderError = failure;
                SetStatus("PreviewFailed", () => failure, InfoBarSeverity.Error);
            }
#endif
            if (file is null || activeSeek == seekVersion)
            {
                RenderRfMeasurement();
                if (file is not null && displayedRfMeasurement is { } reading && playbackPosition != reading.Frames)
                {
                    playbackPosition = reading.Frames;
                    UpdateRfPlaybackBar(true);
                }
            }
            if (!rfPreviewReady && displayedRfMeasurement is not null)
            {
                rfPreviewReady = true;
                UpdateControls();
            }
        };
        try
        {
            ConfigureRfAudioMeter(newSession: true);
            ResetRfMonitor();
#if REALTIME_RF
            if (request?.Ddd is not null || file?.SupportsVideo == true || file?.SupportsHifi == true)
            {
                void SelectDecoder()
                {
                    var type = rfInspector.PreviewOptions.PreviewType;
                    timer.Interval = TimeSpan.FromMilliseconds(type is RfPreviewType.RfOnly or RfPreviewType.VhsHifi ? 100 : 8);
                    presented = 0;
                    rfGuide = null;
                    DiscardRfInspection();
                    ResetPreview(null);
                    ClearRfVideoScopes();
                    ConfigureRfAudioMeter(newSession: true);
                    if (activeAudioMonitor is { } current) current.Enabled = false;
                    activeAudioMonitor = null;
                    var old = Interlocked.Exchange(ref decoder, null);
                    if (old is not null)
                        retiring = retiring.IsCompletedSuccessfully ? old.StopAsync() : Task.WhenAll(retiring, old.StopAsync());
                    if (type == RfPreviewType.RfOnly) return;
                    var format = type switch
                    {
                        RfPreviewType.VideoOnly => PreviewSourceFormat.Cvbs,
                        RfPreviewType.Betamax or RfPreviewType.BetamaxHifi => PreviewSourceFormat.Betamax,
                        RfPreviewType.LaserDisc => PreviewSourceFormat.LaserDisc,
                        _ => PreviewSourceFormat.Vhs
                    };
                    bool pal = format is not (PreviewSourceFormat.Betamax or PreviewSourceFormat.LaserDisc) && rfInspector.PreviewOptions.PalPreview;
                    var options = new RealtimePreviewOptions(format, pal ? PreviewVideoStandard.Pal : PreviewVideoStandard.Ntsc, file?.Rate ?? request!.HardwareRate)
                    {
                        Region = PreviewRegion.FullRaster,
                        DecodeThreads = Math.Min(4, Environment.ProcessorCount),
                        Cvbs50Ohms = format == PreviewSourceFormat.Cvbs && rfInspector.PreviewOptions.RestoreCvbs,
                        BetamaxProfile = type == RfPreviewType.BetamaxHifi ? BetamaxVideoProfile.HiFi : BetamaxVideoProfile.Standard,
                        AudioTrack = format switch
                        {
                            PreviewSourceFormat.Vhs when type == RfPreviewType.VhsHifi => PreviewAudioTrack.VhsHiFi,
                            PreviewSourceFormat.Betamax when type == RfPreviewType.BetamaxHifi => PreviewAudioTrack.BetamaxHiFi,
                            PreviewSourceFormat.LaserDisc => rfInspector.PreviewOptions.LdAudioIndex switch
                            {
                                1 => PreviewAudioTrack.LaserDiscDigitalPcm, 2 => PreviewAudioTrack.LaserDiscDts,
                                3 => PreviewAudioTrack.LaserDiscAc3Rf, _ => PreviewAudioTrack.LaserDiscAnalog
                            },
                            _ => PreviewAudioTrack.None
                        }
                    };
                    if (options.AudioTrack != PreviewAudioTrack.None)
                        activeAudioMonitor = new LocalAudioMonitor(options.AudioTrack is PreviewAudioTrack.LaserDiscDigitalPcm or PreviewAudioTrack.LaserDiscDts ? 44100u : 48000u) { Enabled = rfFileSound, Volume = audioMonitorVolume };
                    Volatile.Write(ref decoder, new RfPreviewDecoder(options, rfAudio, rfInspector.PreviewOptions.AudioTrackLabel, activeAudioMonitor));
                    decoderError = null;
                }
                resetDecoder = SelectDecoder;
                SelectDecoder();
                rfInspector.PreviewOptions.DecoderChanged += resetDecoder;
                offer = (bytes, size, first) => Volatile.Read(ref decoder)?.Offer(bytes, size, first);
            }
#endif
            rfInspector.SetRecording(true);
            RenderSourceRackList();
            UpdateControls();
            timer.Start();
            void Report(RfMeasurement m) => Volatile.Write(ref latestRfMeasurement, m);
            if (file is null)
                await Task.Run(() => session!.Run(vga, Report, stop.Token, offer));
            else
            {
                RfFile.Text = file.Path;
                var ffmpeg = file.ContainerRate == 0 ? null : QcReports.FindTools().FfmpegPath;
                while (!stop.IsCancellationRequested)
                {
                    using var segment = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    playbackSegment = segment;
                    bool seeking = activeSeek != seekVersion;
                    activeSeek = seekVersion;
                    long start = playbackPosition;
                    try
                    {
                        if (seeking)
                        {
                            latestRfMeasurement = displayedRfMeasurement = null;
                            ResetPreview(null);
                            await Task.Delay(100, segment.Token);
#if REALTIME_RF
                            resetDecoder?.Invoke();
#endif
                        }
                        await Task.Run(() => file.Play(start, ffmpeg, Report, offer, segment.Token));
                        if (activeSeek != seekVersion) continue;
#if REALTIME_RF
                        reachedEnd = true;
#endif
                        playbackPosition = file.Samples;
                        RenderRfMeasurement();
                        break;
                    }
                    catch (OperationCanceledException) when (segment.IsCancellationRequested) { }
                    finally { playbackSegment = null; }
                }
            }
            SetStatus("PreviewStopped", "RfReadyHint");
        }
        catch (Exception ex) { SetStatus("PreviewFailed", () => ex.Message, InfoBarSeverity.Error); }
        finally
        {
            timer.Stop();
#if REALTIME_RF
            rfInspector.PreviewOptions.DecoderChanged -= resetDecoder;
            activeAudioMonitor = null;
            await Task.WhenAll(retiring, decoder?.StopAsync(drain: reachedEnd) ?? Task.CompletedTask);
            if (reachedEnd && decoder is not null)
            {
                PresentPreview(decoder.ScopeFeed);
                if (previewFrame?.SourceTag is RfPreviewDecoder.FieldPair finalPair)
                {
                    heldPair = finalPair;
                    if (decoder.ScopeFeed.RenderScopes) decoder.ScopeFeed.VisitScopes(PaintVideoScopes);
                    PaintSignalScope(finalPair, force: true);
                }
                if (decoder.Error is { } error) SetStatus("PreviewFailed", () => error, InfoBarSeverity.Error);
            }
            bool hold = file is not null && heldPair is not null && previewBitmap is not null && decoder is not null;
            heldDecoder = hold ? decoder : null;
            if (hold && stop.IsCancellationRequested && activeSeek == seekVersion)
            {
                var selected = rfInspector.PreviewOptions.FieldMode switch { 2 => heldPair!.Top, 3 => heldPair!.Bottom, _ => heldPair!.First };
                playbackPosition = Math.Clamp((long)Math.Round(selected.Timestamp.TotalSeconds * file!.Rate), 0, file.Samples);
            }
            if (!hold) { heldPair = null; ClearRfVideoScopes(); }
#else
            bool hold = false;
#endif
            if (!hold) { ResetPreview(null); rfGuide = null; }
            rfPreviewReady = false;
            rfPreview = null;
            if (hold) rfAudioTimer.Stop();
            if (!hold) ConfigureRfAudioMeter(newSession: true);
            rfPreviewStop = null;
            rfInspector.SetRecording(rfStop is not null);
            RenderSourceRackList();
            UpdateControls();
        }
    }

    private TelemetryMetric[] RfTelemetry()
    {
        var selection = rfInspector.Selection;
        if (measuredSource is { } source && (source.Ddd != selection?.Ddd || source.Rtl != selection?.Rtl ||
            source.HardwareRate != selection?.HardwareRate || source.Rtl is not null && source.Frequency != selection?.Frequency))
        {
            measuredSource = null;
            ResetRfMonitor();
        }
        var m = displayedRfMeasurement;
        RfMeasureLabel.Text = L("RfPeakSpan");
        RfPeakSpan.Text = m is null ? "—" : m.B is null ? $"{m.A.Max - m.A.Min}" : $"{m.A.Max - m.A.Min} / {m.B.Max - m.B.Min}";
        if (RfMeasureFlyout.IsOpen) RenderRfDetails();
        long clips = (m?.A.Clips ?? 0) + (m?.B?.Clips ?? 0);
        return [
            new("rf-rate", L("RfMetricSampleRate"), (rfInspector.IsPlayback ? rfInspector.PlaybackFile?.Rate : selection?.Rate) is int rate ? $"{rate / 1e6:G} MSPS" : "—"),
            new("rf-clips", L("RfMetricAdcClips"), m is null ? "—" : clips.ToString("N0"),
                clips > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal)
        ];
    }

    private async Task RecordRfAsync(RfRecording request)
    {
        using var stop = new CancellationTokenSource();
        rfStop = stop;
        var preview = rfPreview;
        string prefix = Path.Combine(OutputPath.Text.Trim(), RecordingFileNaming.BuildPrefix(GetRecordingLabel(), DateTime.Now));
        string path = prefix + request.Suffix;
        var vga = rfInspector.VgaReader();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        bool received = false;
        void ReportWriter(RfMeasurement m)
        {
            if (preview is null) Volatile.Write(ref latestRfMeasurement, m);
            Volatile.Write(ref received, true);
        }
        timer.Tick += (_, _) =>
        {
            if (!rfWriterActive && Volatile.Read(ref received))
            {
                rfWriterActive = true;
                RenderSourceRackList();
            }
            if (preview is null) RenderRfMeasurement();
            UpdateRecordingClock();
        };
        try
        {
            rfInspector.SetRecording(true);
            RenderSourceRackList();
            if (preview is null) ResetRfMonitor();
            RenderCounts(null);
            RfFile.Text = path;
            timer.Start();
            displayedRecordingSeconds = -1;
            recordingClock.Restart();
            UpdateRecordingClock();
            UpdateControls();
            UpdateRecordingIndicator(true);
            SetStatus("RecordingActive", () => path);
            var ffmpeg = request.Flac ? QcReports.FindTools().FfmpegPath : null;
            var qc = await Task.Run(() => preview is not null ? preview.Record(prefix, ffmpeg, stop.Token, ReportWriter) : request.Capture(prefix, ffmpeg, vga, ReportWriter, stop.Token));
            if (qc is null)
                SetStatus("RecordingSaved", () => path, InfoBarSeverity.Success);
            else
            {
                rfQcTail = RunRfQcAsync(rfQcTail, qc);
                SetStatus("RfQcQueued", () => path);
            }
        }
        catch (Exception ex)
        {
            RfFile.Text = ex.Message;
            SetStatus("RecordingFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            timer.Stop();
            RenderRfMeasurement();
            recordingClock.Reset();
            UpdateRecordingIndicator(false);
            rfWriterActive = false;
            rfStop = null;
            rfInspector.SetRecording(rfPreview is not null);
            RenderSourceRackList();
            UpdateControls();
        }
    }

    private Task rfQcTail = Task.CompletedTask;

    private async Task RunRfQcAsync(Task previous, RfRampQc qc)
    {
        var entry = new QcSessionHistoryEntry(Guid.NewGuid(), qc.FinalPath + ".partial", Path.GetFileName(qc.FinalPath),
            QcEntryKind.RfRamp, DateTimeOffset.Now, "RfQcQueued", "", TelemetrySeverity.Info, []);
        void Show(string titleKey, TelemetrySeverity severity, string detail, string path)
        {
            entry = entry with { TitleKey = titleKey, Severity = severity, Summary = detail, FilePath = path };
            var selected = QcCenterPage.DisplayedEntry;
            qcHistory.Record(entry);
            latestQcEntry = entry;
            if (qcStop is null && (selected is null || selected.Id == entry.Id))
                QcCenterPage.RenderEntry(entry);
            QcCenterPage.RenderHistory(qcHistory.Entries, QcCenterPage.DisplayedEntry?.Id);
            RenderQcSummaryBadge();
            UpdateQcNavBadge();
        }

        string samples = LocalFormat("RfQcSamples", qc.FileSamples);
        Show("RfQcQueued", TelemetrySeverity.Info, samples, entry.FilePath);
        await previous;
        Show("RfVerifyRamp", TelemetrySeverity.Info, samples, entry.FilePath);
        try
        {
            await Task.Run(qc.VerifyAndPublish);
            Show("QcPassed", TelemetrySeverity.Normal, samples, qc.FinalPath);
        }
        catch (Exception ex)
        {
            Show("QcFailed", TelemetrySeverity.Danger, ex.Message, entry.FilePath);
        }
    }

    private void RfMeasureFlyout_Opened(object sender, object args) => RenderRfDetails();

    private void RenderRfDetails()
    {
        var m = displayedRfMeasurement;
        bool iq = m is null ? (rfInspector.IsPlayback ? rfInspector.PlaybackFile?.IsIq == true : rfInspector.Selection?.Rtl is not null) : m.B is not null;
        RfMeasureHeading.Text = L("RfMeasureHeading");
        RfChannelA.Text = iq ? "I" : "ADC";
        RfChannelB.Text = iq ? "Q" : "";
        void Column(RfLevels? ch, TextBlock min, TextBlock max, TextBlock pp, TextBlock rms, TextBlock clips, string empty)
        {
            min.Text = ch?.Min.ToString(settingsCulture) ?? empty;
            max.Text = ch?.Max.ToString(settingsCulture) ?? empty;
            pp.Text = ch is null ? empty : (ch.Max - ch.Min).ToString(settingsCulture);
            rms.Text = ch?.Rms.ToString("F1", settingsCulture) ?? empty;
            clips.Text = ch?.Clips.ToString("N0", settingsCulture) ?? empty;
        }
        Column(m?.A, RfMinA, RfMaxA, RfPpA, RfRmsA, RfClipsA, "—");
        Column(m?.B, RfMinB, RfMaxB, RfPpB, RfRmsB, RfClipsB, iq ? "—" : "");
        RfReading.Text = LocalFormat("RfInputSamples", m?.Frames.ToString("N0", settingsCulture) ?? "—",
            ((rfInspector.IsPlayback ? rfInspector.PlaybackFile?.Rate : rfInspector.Selection?.HardwareRate) / 1e6)?.ToString("G", settingsCulture) ?? "—");
        RfFile.Visibility = string.IsNullOrEmpty(RfFile.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool ShowRfPcmSpectrum => rfAudio is not null && RfSpectrumSource.SelectedIndex == 1;

    private void UpdateRfSpectrumSource()
    {
        RfSpectrumSource.Visibility = IsRfActive && rfAudio is not null && SpectrumBody.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        if (rfAudio is null && RfSpectrumSource.SelectedIndex == 1) RfSpectrumSource.SelectedIndex = 0;
    }

    private void RfSpectrumSource_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!settingsReady || !IsRfActive) return;
        ResetRfSpectrum();
        if (!ShowRfPcmSpectrum && displayedRfMeasurement is { } reading) PaintRfSpectrum(reading);
    }

    private void ResetRfSpectrum()
    {
        spectrumPeakHoldValues = null;
        SpectrumTrace.Points.Clear();
        SpectrumPeakHold.Points.Clear();
        SpectrumFill.Points.Clear();
        PaintSpectrumScale(null);
        SpectrumMinFrequency.Text = "0 Hz";
        SpectrumMidFrequency.Text = SpectrumMaxFrequency.Text = "—";
    }

    private void PaintRfSpectrum(RfMeasurement measurement)
    {
        var trace = measurement.A.Trace;
        var q = measurement.B?.Trace;
        var rate = rfInspector.IsPlayback ? rfInspector.PlaybackFile?.Rate : rfInspector.Selection?.HardwareRate;
        if (rate is null || trace.Length < 2) return;
        var bins = new System.Numerics.Complex[trace.Length];
        double windowSum = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (bins.Length - 1));
            windowSum += window;
            bins[i] = new System.Numerics.Complex(trace[i], q?[i] ?? 0) * (window / measurement.FullScale);
        }
        StereoMonitor.ForwardFft(bins);
        int half = bins.Length / 2;
        var levels = new float[half + 1 + (q is null ? 0 : half)];
        const double floor = -90;
        for (int i = 0; i < levels.Length; i++)
        {
            int bin = q is null ? i : (i + half) % bins.Length;
            double gain = q is null && i != 0 && i != half ? 2 : 1;
            levels[i] = (float)Math.Max(floor, 20 * Math.Log10(bins[bin].Magnitude * gain / windowSum));
        }
        SpectrumMinFrequency.Text = q is null ? "0 Hz" : $"{-rate / 2e6:G4} MHz";
        SpectrumMidFrequency.Text = q is null ? $"{rate / 4e6:G4} MHz" : "0 Hz";
        SpectrumMaxFrequency.Text = $"{rate / 2e6:G4} MHz";
        SecondaryInstrumentSubtitle.Text = q is null ? "RF · ADC" : "RF · I/Q";
        if (SpectrumLevelAxis.Children.Count == 0) PaintSpectrumScale(floor);
        PaintSpectrum(levels, floor);
    }

    private void RenderRfMeasurement()
    {
        var m = Interlocked.Exchange(ref latestRfMeasurement, null);
        if (m is null) return;
        measuredSource = rfInspector.Selection;
        displayedRfMeasurement = m;
        if (!ShowRfPcmSpectrum && SpectrumBody.Visibility == Visibility.Visible && InstrumentsHost.Visibility == Visibility.Visible)
            PaintRfSpectrum(m);
        RenderCounts(null);
        RfMeterA.Show(m.B is null ? "ADC" : "I", m.A, m.FullScale);
        if (m.B is not null) RfMeterB.Show("Q", m.B, m.FullScale);
        if (RfScopeSource.SelectedIndex == 0) RfTraceA.Points = RfAdcTrace(m.A, m.FullScale);
        if (m.B is not null && RfScopeSource.SelectedIndex == 0) RfTraceB.Points = RfAdcTrace(m.B, m.FullScale);
        var telemetry = new List<string>();
        if (m.Fifo is { } f)
        {
            string limit = f.CoverageLimited || m.FifoDisabled ? "≥" : "";
            telemetry.Add(LocalFormat("RfFifoLevels", f.UsedWords, f.DepthWords, f.RunPeakWords,
                100d * f.RunPeakWords / f.DepthWords, limit, f.NearFullSamples));
        }
        if (m.LegacyMarkerState is { } state) telemetry.Add(L("RfMarker" + state));
        if (m.FifoDisabled) telemetry.Add(L("RfFifoStopped"));
        rfInspector.SetTelemetry(string.Join(Environment.NewLine, telemetry));
    }
}
