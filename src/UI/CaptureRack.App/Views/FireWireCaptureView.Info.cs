using System.Diagnostics;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.QualityControl;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace CaptureRack.App.Views;

public sealed partial class FireWireCaptureView
{
    private long lastInfoRender;
    private string? videoDetails;

    private void BindStreamInfo()
    {
        InfoHeading.Text = L("FwInfoHeading").ToUpperInvariant();
        DeviceSectionTitle.Text = L("FwInfoDeviceSection");
        ProgramSectionTitle.Text = L("FwInfoProgramSection");
        StatsSectionTitle.Text = L("FwInfoStatsSection");
        RawSectionTitle.Text = L("FwInfoRawSection");
        CopyInfoLabel.Text = L("FwInfoCopy");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CopyInfoButton, L("FwInfoCopy"));
        RefreshStreamInfo(force: true);
    }

    private void InfoSectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: string tag })
            return;

        var (body, chevron) = tag switch
        {
            "Device" => (DeviceSectionBody, DeviceSectionChevron),
            "Program" => (ProgramSectionBody, ProgramSectionChevron),
            "Stats" => (StatsSectionBody, StatsSectionChevron),
            _ => (RawSectionBody, RawSectionChevron)
        };
        var expanded = body.Visibility != Visibility.Visible;
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
        if (expanded)
            Controls.StudioFeedback.RevealSubtle(body, offsetY: 4f, durationMs: 120);
    }

    internal string? PreviewTransportSummary
    {
        get
        {
            if (session?.TransportInfo is not { } info)
                return session?.DvInfo?.LastFrame?.Timecode is { } timecode ? $"DV · TC {timecode}" : null;
            var mode = info.ModeKey is { } key ? L(key) : L("FwInfoUnavailable");
            return info.Counter is { } counter ? $"i.LINK · {mode} · {L("FwInfoCounter")} {counter}" : $"i.LINK · {mode}";
        }
    }

    internal void RefreshStreamInfo(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(lastInfoRender, now).TotalMilliseconds < 450)
            return;
        lastInfoRender = now;
        var live = session;
        var stream = live?.StreamInfo;
        var dv = live?.DvInfo;
        var transport = live?.TransportInfo;
        FormatBadgeText.Text = FormatSummary;
        ProgramSectionTitle.Text = L(ExpectedFormat.IsDv ? "DvInfoMetadata" : "FwInfoProgramSection");
        InfoSummary.Text = string.Join(" · ", new[]
        {
            PreviewTransportSummary ?? L(live?.SupportsTransportInfo == false ? "FwInfoUnsupported" : "FwInfoWaiting"),
            stream is not null || dv is not null ? Volatile.Read(ref videoDetails) : null
        }.OfType<string>());

        var fields = new List<string>();
        if (DevicePicker.SelectedItem is FireWireDevice device)
            fields.Add($"{device.Name} · {device.Id}");
        if (transport is null)
            fields.Add(L(live?.SupportsTransportInfo == false ? "FwInfoUnsupported" : "FwInfoWaiting"));
        else
        {
            foreach (var field in transport.Fields)
            {
                var value = field.State switch
                {
                    FireWireReadState.Unsupported => L("FwInfoUnsupported"),
                    FireWireReadState.Unavailable => L("FwInfoUnavailable"),
                    _ when field.Key == "FwInfoMode" && transport.ModeKey is { } mode => L(mode),
                    _ when field.Key == "FwInfoMediaPresent" => L(field.Value == "0" ? "FwInfoNo" : "FwInfoYes"),
                    _ => field.Value ?? L("FwInfoUnavailable")
                };
                fields.Add(Row(field.Key, value));
            }
            fields.Add(Row("FwInfoRemaining", L("FwInfoUnavailable")));
            fields.Add(Row("FwInfoReadAt", transport.ReadAt.ToString("HH:mm:ss")));
        }
        DeviceDetails.Text = string.Join("\n", fields);

        if (dv is not null)
        {
            ProgramDetails.Text = FormatDvMetadata(dv.LastFrame);
            StatsDetails.Text = string.Join("\n", Row("DvInfoFrames", dv.Frames.ToString("N0")),
                Row("DvInfoInvalidFrames", dv.InvalidFrames.ToString("N0")),
                Row("DvInfoFormatChanges", dv.FormatChanges.ToString("N0")),
                Row("DvInfoDiscontinuities", dv.Discontinuities.ToString("N0")));
        }
        else if (stream is null)
        {
            ProgramDetails.Text = StatsDetails.Text = L("FwInfoWaiting");
        }
        else
        {
            ProgramDetails.Text = FormatPrograms(stream);
            StatsDetails.Text = FormatStats(stream);
        }
        var raw = transport?.Fields.Select(field => Row(field.Key, field.Raw)).ToList() ?? [];
        if (stream is not null)
        {
            foreach (var program in stream.Programs)
                foreach (var track in program.Tracks)
                    raw.Add($"{L("FwInfoProgram")} {program.Number} · PID {Pid(track.Pid)} · stream_type 0x{track.StreamType:X2}");
            raw.Add(Row("FwInfoPrefix", stream.PacketPrefix ?? "—"));
            raw.Add(Row("FwInfoServicePids", string.Join(", ", stream.ServiceTablePids.Select(Pid))));
            raw.Add(Row("FwInfoReadAt", stream.ReadAt.ToString("HH:mm:ss")));
        }
        if (dv?.LastFrame is { } frame)
            raw.Add($"DIF · {frame.FrameBytes:N0} B · {dv.InputBytes:N0} B");
        RawDetails.Text = raw.Count == 0 ? L("FwInfoWaiting") : string.Join("\n", raw);
        CopyInfoButton.IsEnabled = stream is not null || dv is not null || transport is not null;
    }

    private string FormatDvMetadata(CaptureRack.QualityControl.DvFrameInfo? frame)
    {
        if (frame is null)
            return L("FwInfoWaiting");
        var unknown = L("FwInfoNotSeen");
        return string.Join("\n", $"{frame.Encoding.DisplayName()} · {frame.Width} × {frame.Height} · {frame.FramesPerSecond:0.###} fps",
            Row("DvInfoTimecode", frame.Timecode ?? unknown),
            Row("DvInfoRecordingDate", frame.RecordingDate ?? unknown),
            Row("DvInfoRecordingTime", frame.RecordingTime ?? unknown),
            Row("DvInfoAudio", frame.AudioRate is { } rate
                ? $"{rate / 1000d:0.###} kHz · {frame.AudioBits?.ToString() ?? "—"} bit" +
                    (frame.AudioChannels is { } channels ? $" · {channels} ch" : "")
                : unknown),
            Row("DvInfoAspect", frame.Widescreen switch { true => "16:9", false => "4:3", null => unknown }));
    }

    private string FormatPrograms(FireWireTsInfo stream)
    {
        var absent = L("FwInfoNotSeen");
        var output = new List<string>();
        foreach (var program in stream.Programs)
        {
            if (output.Count != 0)
                output.Add("");
            output.Add($"{L("FwInfoProgram")} {program.Number} · PMT {Pid(program.PmtPid)} · PCR {(program.PcrPid is >= 0 and < 8191 ? Pid(program.PcrPid.Value) : "—")}");
            output.Add(Row("FwInfoServiceName", program.ServiceName ?? absent));
            output.Add(Row("FwInfoEventName", program.EventName ?? absent));
            output.Add(Row("FwInfoBroadcastStart", program.BroadcastStart ?? absent));
            foreach (var track in program.Tracks)
            {
                string? selected = null, details = null;
                if (decoder?.VideoPid == track.Pid)
                {
                    selected = L("FwInfoPreviewTrack");
                    details = Volatile.Read(ref videoDetails);
                }
                else if (decoder?.AudioPid == track.Pid)
                {
                    selected = L("FwInfoPreviewTrack");
                    if (AudioInfo is { } audio)
                        details = $"{audio.Codec} · {audio.BitDepth?.ToString() ?? "—"} bit · {(audio.SampleRateHz is { } sampleRate ? $"{sampleRate / 1000d:0.###}" : "—")} kHz · {string.Format(L("PreviewAudioChannels"), audio.Channels?.ToString() ?? "—")} · {(audio.BitRateEstimated ? "≈" : "")}{(audio.BitRate is { } bitRate ? $"{bitRate / 1000d:0.###}" : "—")} kb/s";
                }
                output.Add(string.Join(" · ", new[] { Pid(track.Pid), TrackType(track.StreamType), track.Language, selected, details }.OfType<string>()));
            }
        }
        if (output.Count == 0)
            output.Add(L("FwInfoWaiting"));
        output.Add(Row("FwInfoBroadcastClock", stream.BroadcastTime ?? absent));
        if (stream.Network is { } network)
            output.Add(Row("FwInfoNetwork", network));
        return string.Join("\n", output);
    }

    private string FormatStats(FireWireTsInfo stream)
    {
        var rows = new List<string>
        {
            Row("FwInfoSegment", stream.Segment.ToString()),
            Row("FwInfoDuration", stream.ElapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).ToString("c") : "—"),
            Row("FwInfoRate", stream.BitsPerSecond is > 0 ? $"{stream.BitsPerSecond / 1000:N0} kb/s" : "—"),
            Row("TsMetricPackets", $"{stream.Packets:N0}"),
            Row("TsMetricContinuity", $"{stream.ContinuityErrors:N0}"),
            Row("FwInfoMalformed", $"{stream.MalformedPackets:N0}"),
            Row("TsMetricSync", $"{stream.SyncErrors:N0}"),
            Row("FwInfoTransportErrors", $"{stream.TransportErrors:N0}"),
            Row("FwInfoClockJumps", $"{stream.ClockJumps:N0}"),
            "",
            L("FwInfoPidColumns")
        };
        rows.AddRange(stream.Pids.Select(pid => $"{Pid(pid.Pid)}  ·  {pid.Packets:N0}  ·  {pid.ContinuityErrors:N0}  ·  {pid.TransportErrors:N0}"));
        return string.Join("\n", rows);
    }

    private string Row(string key, string value) => $"{L(key)}  {value}";
    private static string Pid(int pid) => $"0x{pid:X4}";
    private string TrackType(int type) => type switch
    {
        1 => "MPEG-1 Video",
        2 => "MPEG-2 Video",
        3 => "MPEG-1 Audio",
        4 => "MPEG-2 Audio",
        6 => L("FwInfoPrivateData"),
        0x0d => "DSM-CC sections",
        0x0f => "AAC",
        0x11 => "AAC LATM",
        0x1b => "H.264",
        0x24 => "HEVC",
        _ => $"stream_type 0x{type:X2}"
    };

    private void CopyInfoClicked(object sender, RoutedEventArgs e)
    {
        RefreshStreamInfo(force: true);
        var snapshot = new DataPackage();
        snapshot.SetText(string.Join("\n\n", InfoHeading.Text, InfoSummary.Text,
            $"{DeviceSectionTitle.Text}\n{DeviceDetails.Text}", $"{ProgramSectionTitle.Text}\n{ProgramDetails.Text}",
            $"{StatsSectionTitle.Text}\n{StatsDetails.Text}", $"{RawSectionTitle.Text}\n{RawDetails.Text}"));
        Clipboard.SetContent(snapshot);
        Controls.StudioFeedback.FlashCopied(CopyInfoIcon, "\uE8C8", CopyInfoLabel, L("CopiedFeedback"), () => L("FwInfoCopy"), this);
    }
}
