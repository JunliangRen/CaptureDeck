# CaptureRack

CaptureRack is a .NET 11 video capture and preservation tool. The Windows desktop app uses native WinUI 3 and captures directly from the Blackmagic DeckLink API. The separate Uno Desktop prototype runs offline MOV and TS quality control on Windows and Apple Silicon Macs using the same QC engine.

## Builds

| Platform | Build | Features |
| --- | --- | --- |
| Windows x64 | [Native WinUI 3](../../actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess) | DeckLink and FireWire preview and recording, plus offline QC. |
| Windows ARM64 | [Native WinUI 3](../../actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess) | Offline QC. DeckLink capture is unavailable. |
| macOS ARM64 | [Uno Desktop prototype](../../actions/workflows/uno-macos.yml?query=branch%3Amain+is%3Asuccess) | Offline MOV and TS QC, report viewing, and TS JSON export |

Open a successful run and download the matching package from **Artifacts**. The Windows artifacts contain the app and managed command-line probe. Run `Probe/CaptureRack.Probe.exe firewire` to access the FireWire commands. The macOS artifact contains the Apple Silicon test app.

## Project structure

```text
CaptureRack/
|-- src/
|   |-- UI/
|   |   |-- CaptureRack.App              # Native WinUI desktop app
|   |   `-- CaptureRack.Uno              # Uno Desktop offline QC prototype
|   |-- Capture/
|   |   |-- CaptureRack.Core             # Shared capture contracts and backend registry
|   |   |-- CaptureCards/
|   |   |   `-- CaptureRack.DeckLink     # Blackmagic DeckLink SDK capture, preview, and MOV recording
|   |   |-- CaptureRack.DirectShow/      # Windows DirectShow capture
|   |   |   `-- Windows/
|   |   |       |-- FireWire/           # IEEE 1394 / i.LINK capture sources
|   |   |       |   `-- MsTape/         # D-VHS device discovery and MPEG-TS capture
|   |   |       `-- Interop/            # Shared COM bindings, media types, and sample delivery
|   |   |-- CaptureRack.SignalCapture    # Sampled-signal capture devices and diagnostics
|   |   `-- CaptureRack.Audio            # Reserved for standalone audio capture
|   |-- Processing/
|   |   |-- CaptureRack.Recording       # Shared transport-stream recording
|   |   `-- CaptureRack.QualityControl  # Capture validation and reports
|   |-- CaptureRack.Probe               # Command-line device probe and QC
|   `-- CaptureRack.DeviceControl       # Independent tape-transport contracts
`-- test/
    |-- CaptureRack.UT               # Windows capture and settings tests
    `-- CaptureRack.QualityControl.UT # Cross-platform TS analysis tests
```

Capture backends that call a capture-card vendor SDK live under `src/Capture/CaptureCards/`. SignalCapture keeps device-specific rules in `Devices/Domesday/` and `Devices/Sdr/RtlSdr/` and Windows USB transport in `Windows/`. DeckLink groups SDK bindings, capture, monitoring, and recording into `Interop/`, `Capture/`, `Monitoring/`, and `Recording/`.

FireWire / i.LINK and general DirectShow devices are separate capture sources. The Windows MSTape implementation lives under `CaptureRack.DirectShow/Windows/FireWire/MsTape/`. Reusable COM bindings and DirectShow media-type structures live in `CaptureRack.DirectShow/Windows/Interop/`. The general DirectShow probe uses the same COM bindings and managed sink.

Capture backends discover devices, negotiate formats, and receive samples. Downstream processing is organized by media format so multiple backends can use the same writer or analyzer. MSTape negotiates its supported packet layout and passes the original bytes to `Processing/CaptureRack.Recording/TransportStream/`. The writer handles buffering, byte-preserving output, and file finalization. It has no dependency on DirectShow or MSTape. `CaptureRack.QualityControl` provides the TS parser for both recording-time validation and offline file checks. Tape transport controls belong to `CaptureRack.DeviceControl`.

The shared recording project currently contains the Windows TS writer. Future sources that deliver TS can use it with their own packet layout. DV handling, shared frame and PCM processing, and CVBS/RF preview decoding can be added as separate paths when their sources are connected.

Capture and recording share the `CaptureSampleHandler` callback in `CaptureRack.Core`. DirectShow delivers bytes in the format negotiated at session start, with an optional stream timestamp and a discontinuity flag. Sample bytes are borrowed for the duration of the callback. A consumer that queues data copies it into its own buffer. The TS writer uses this callback for both MSTape and file replay in tests and retains its bounded queue and byte-preserving output. Other backends can adopt the contract with their own format negotiation.

**Available today.** DeckLink is the registered desktop capture backend. The desktop app supports DeckLink monitoring and MOV recording, plus FireWire MPEG-TS preview and recording on Windows x64. The command-line probe also captures FireWire MPEG-TS. The probe enumerates devices and checks input, and both entry points can run post-recording QC. Existing flat settings load as a DeckLink profile and move to the nested format on the next save.

**Possible future integrations.**

- **Other capture-card SDKs** can register alongside DeckLink using their own stable backend IDs. Each backend handles its own device discovery, format negotiation, and capture options.
- **General DirectShow capture** can support Windows cameras, video capture cards, and audio inputs that expose compatible filters.
- **FireWire** can preview and record MSTape MPEG-TS in the desktop app and capture TS from the command line. Native DV is a separate format.
- **Signal capture** can enumerate and probe Domesday Duplicator devices via WinUSB and inspect or configure RTL-SDR receivers via librtlsdr on Windows. MISRC support, raw sample recording, and live monitoring are future integrations.
- **Audio** is reserved for standalone audio inputs such as ASIO and WASAPI.
- **Device control** is independent of the capture source. The transport contract is ready for future AV/C, JVC JLIP, and other RS-232 protocols. It distinguishes command acceptance from observed device state. Recording to the computer and recording to tape are separate operations.

Run the unit tests from the repository root.

```powershell
dotnet test test/CaptureRack.UT/CaptureRack.UT.csproj -c Release
dotnet test test/CaptureRack.QualityControl.UT/CaptureRack.QualityControl.UT.csproj -c Release
```

## Windows builds

The default build targets Windows x64 (Intel/AMD). Publish the app and probe for the architecture that will run them.

```powershell
$rid = 'win-arm64' # Or 'win-x64'
dotnet publish src/UI/CaptureRack.App -c Release -r $rid --self-contained
dotnet publish src/CaptureRack.Probe -c Release -r $rid --self-contained
```

On Windows ARM64, CaptureRack can open settings and check existing MOV files. DeckLink capture is currently available on Windows x64 with a compatible Blackmagic Desktop Video installation. Device support for future backends is validated separately.

## Windows RF USB probe

```powershell
dotnet build src/CaptureRack.Probe -c Release
$probe = '.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe'
& $probe rf list
& $probe rf probe '<instance-id from rf list>' --json
```

The probe targets Domesday Duplicator firmware through 3.2 using the legacy USB ID `1D50:603B` or the protocol-v1 runtime ID `1209:2347`. It identifies the protocol using the device descriptor and verifies the register map and application image for protocol-v1. A USB protocol version does not identify the physical board revision. Use the Windows WinUSB driver for the selected device. The probe reports the active driver and opens only the selected runtime instance.

Probing reads USB descriptors, the stream endpoint, and protocol-v1 firmware identity. It does not start collection, write device registers, rebind drivers, or enter bootloader mode. Device capture and preview are outside this probe. See the [Domesday Duplicator Windows setup guide](https://domesday86.github.io/DomesdayDuplicator/capture-gui/install-msi/) for driver binding.

## Windows RTL-SDR probe

RTL-SDR uses a matching-architecture `rtlsdr.dll` or `librtlsdr.dll` beside the probe, or an absolute path set in `CAPTURERACK_RTLSDR_LIBRARY`. Place its native dependencies next to the library and bind the receiver to WinUSB. This optional library is supplied separately.

```powershell
& $probe rf sdr rtl-sdr list
& $probe rf sdr rtl-sdr probe 0
& $probe rf sdr rtl-sdr configure 0 --frequency 1500000 --sample-rate 1024000 --gain 0 --agc off --vga 8 --json
```

`probe` shows the tuner and its supported manual gain values. `configure` applies the settings for a diagnostic session and closes the receiver without collecting I/Q. Defaults are 100 MHz, 2.4 MSPS, auto tuner gain, and digital AGC off. Set manual gain in tenths of a dB (for example, 197 means 19.7 dB). Frequency correction is optional (`--ppm`). Manual VGA indices 0–8 are enabled only for the R828D-based RTL-SDR Blog V4 with the patched librtlsdr exports used by [MISRC-GUI](https://github.com/harrypm/MISRC-GUI/blob/main/docs/RTL-SDR.md). The manual VGA default is 8 when supported.

## DirectShow device capabilities

In the Windows app, open the source selector and choose **DirectShow Capture**. Pick a device to inspect its video streams and their formats. Choose an audio pin and format when available. The shared right capture panel shows driver-reported formats, the negotiated formats, and sample counts after a two-second test. Start preview to view the selected stream live. Supported preview formats include YUY2, UYVY, NV12, I420, YV12, RGB24/32, MJPEG, v210, r210, and 8/16-bit infrared. The shared stereo meter and spectrum are available for supported PCM inputs. Decoded video uses the shared waveform, RGB parade, and vectorscope. Speaker monitoring is optional and uses the first two channels. Infrared formats are listed as a separate video stream when advertised by the driver.

With preview or MPEG-2 input running, select **Start recording** to preserve the negotiated DirectShow samples. Compressed MJPEG and MPEG-2 elementary video try TS, MOV, MP4, then MKV. Raw formats try MOV, MP4, then MKV. PCM audio remains in its negotiated format. Capture stages to a recoverable .mkv.partial file, then tries each container at stop using FFmpeg stream copy and verifies stream formats, SHA-256 payload hashes, and the first decoded video frame. The app keeps MKV when FFmpeg/FFprobe are unavailable or no earlier candidate passes verification. Preview continues while the output is finalized. Leave extra space for one additional copy during finalization.

The DirectShow workspace shows container attempts and writes a .recording.json report beside the output. The report includes negotiated tracks, written sample counts, timestamp anomalies, and container decisions. Stopping a recording runs the shared QC workflow. MOV and MKV go through full decode in QC Center. QC compares the report with the file when available and generates FrameMD5 and QCTools reports. Transport-stream analysis remains a separate check.

Keep the release package's `App` and `Probe` folders side by side. The app uses the probe as an isolated diagnostic process.

## Windows DirectShow video-device probe

Run these commands from the native probe package. Device names are prefixed with [DirectShow]. Use the device index from list or pass its quoted moniker ID. The media-type indexes come from probe for the selected device.

    CaptureRack.Probe.exe directshow list
    CaptureRack.Probe.exe directshow probe 0 --json
    CaptureRack.Probe.exe directshow negotiate 0 <video-index> [audio-index] --json
    CaptureRack.Probe.exe directshow capture 0 5 <video-index> [audio-index] --json --trace

The probe enumerates video-input filters and their advertised output-pin formats, including audio pins on those filters. MSTape stays in the FireWire command group. The first reference device is Blackmagic WDM Capture on a DeckLink Studio 4K. These commands use DirectShow throughout. They leave hardware input routing as configured.

Negotiate connects the selected pins directly to managed sinks, reads the negotiated media types, and leaves the graph stopped. Capture starts the graph for a bounded interval and reports sample sizes, timestamps, discontinuities, and a driver drop count when available. It diagnoses sample delivery without creating media files. Exit code 2 means at least one requested stream delivered no samples. Exit code 124 means a driver call exceeded the command deadline. Use a matching live input signal for capture. Unknown field order is kept as unknown when the media header does not supply it.

## Windows x64 FireWire / i.LINK

Choose **FireWire / i.LINK (MSTape)** from the source selector, select the device in the right capture panel, and use the shared bottom bar to start preview. Start playback on the deck. FFmpeg decodes video and audio for the shared preview, Bob-Weave controls, scopes, stereo meter, spectrum, and optional speaker monitoring. Choose an output folder, then start recording after TS data arrives. The .ts192 file preserves the original transport bytes. Recording can run with FFmpeg unavailable. Stopping a recording runs the shared TS check and opens its results from the bottom bar.

The i.LINK inspector shows the tape counter and deck state when the driver returns them. It also shows MPEG-TS programs, tracks, available broadcast metadata, per-PID statistics, and a copyable diagnostic snapshot. During a tape gap or program change, preview re-probes the stream and updates the displayed format and tracks. TS192 recording continues to preserve the incoming bytes.

### Command-line probe

The .NET 11 probe includes a FireWire command group. It calls MSTape/DirectShow on Windows x64 and can analyze TS files without a deck.

```powershell
dotnet build src/CaptureRack.Probe -c Release
$probe = '.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe'
& $probe firewire list
& $probe firewire probe --device '<device-id>'
& $probe firewire capture --device '<device-id>' --output 'D:\Captures\tape.ts192' --format ts192
& $probe firewire analyze --input 'D:\Captures\tape.ts192' --json
```

Copy the device ID from `list`. Use an existing output directory. Once the graph starts, play the tape manually. Ctrl+C stops PC capture, drains queued samples, and finalizes the file. `--duration` limits capture from the first sample. `--no-data-timeout` sets the idle timeout in seconds (default 30).

- Live capture requires MSTape to deliver native 192-byte packets. Omitting `--format` and specifying `--format ts192` select the same layout. Offline `analyze` accepts both 188- and 192-byte files.
- Files contain the bytes delivered by MSTape, including the four-byte prefix in the 192-byte layout. Capture writes to `<output>.partial` before finalization and retains the partial file on failure. Existing files are protected from overwrites.
- The sidecar `<output>.report.json` records file and queue accounting, transport sync, malformed packets, TEI, continuity, PCR and PTS statistics. It includes zero-based packet indexes for the first error of each reported kind. PCR checks account for wraparound and signaled discontinuities. The report covers transport integrity, and separate playback is needed to verify decoding.
- `analyze` inspects an existing recording and prints a summary or JSON with `--json`. It auto-detects aligned input with at least five valid packets at the beginning. Specify `--format` for short files or files with a damaged beginning. Structural errors and a truncated tail return exit code 4. TEI, continuity, and backward PCR counts are quality diagnostics.

The .NET tests use generated packets and an in-process DirectShow source. Live TS192 capture from the HM-DHX2 has been verified.

## Native Windows desktop app

Build and launch from the repository root with the .NET 11 SDK. Install Blackmagic Desktop Video on x64 to capture from DeckLink.

```powershell
dotnet build CaptureRack.slnx -c Release
.\src\UI\CaptureRack.App\bin\Release\net11.0-windows10.0.26100.0\win-x64\CaptureRack.App.exe
```

Select a device, video source, audio source, and video format. The source lists come from the DeckLink SDK and the mode list follows the selected video source and format. **Start preview** monitors the input. To record, start preview and choose an existing output folder. With Auto detect, recording is available when the card delivers readable frames in the active preview mode. Without a signal, the app labels that mode as a fallback and allows recording no-input frames. An input mode change during an Auto detect recording ends the clip in its original mode, runs QC, and lets preview follow the new mode. **Start recording** names the MOV with the local start time and selected formats, for example `CaptureRack_20261124_060236_633_10bit-Uncompressed_16bit-PCM.mov`. **Stop recording** finalizes the MOV and runs QC while preview continues. The app applies the selected routing for the session and restores the previous routing on stop.

Open **Settings** from the gear button or the configuration summary in the bottom bar. CaptureRack remembers the selected device, inputs, mode, formats, output folder, and settings language in `%LOCALAPPDATA%\CaptureRack\settings.json`. Startup restores the selections and waits for you to start preview. The first launch uses the Windows display language, with English as the fallback, and saves the resolved selection. Later launches use the saved app language. Use the fixed back button, the mouse back button, or Alt+Left to return to capture. The configuration page groups input, output, and appearance. Expand **Recording formats** to change the video or PCM format. The bottom bar keeps preview and recording controls together and shows a recording indicator and elapsed time while writing. Open the compact status button for signal details and the active filename. The telemetry row shows frame counts, free space, and estimated recording time from the selected mode and PCM format. The estimate excludes MOV and QC report overhead and depends on the free space available when it was checked.

The two MOV video options request uncompressed 10-bit 4:2:2 (`v210`, default) or 8-bit 4:2:2 (UYVY, `2vuy`) directly from the SDK. **Audio format** selects native 48 kHz stereo signed PCM, either 16-bit (default) or 32-bit. Preview requests 32-bit PCM when supported, so its spectrum uses the SDK input sample depth. A 16-bit recording from 32-bit input stores the high signed 16 bits of each sample. Matching input and recording depths are written directly. The MOV carries the selected mode's frame rate and interlaced field order. Readable no-input frames from the card are included in the recording and counted separately from valid video.

DeckLink's **Capture VANC input line** option maps up to three configured source lines into the top rows of the video on supported inputs. For NTSC the frame remains 720 × 486. **Scan and fill** reads available ancillary data and can temporarily rotate the mappings when direct reads find no useful lines. The scanner restores the previous mappings and fills source line numbers when it finds evidence. Turning on the mapping is a separate control.

The native WinUI preview converts either format to BGRA. The preview information shows the input codec, pixel format, scan and field order, and available audio codec, sample rate, channels, bit depth, and bitrate. The preview menu offers motion-adaptive Bob-Weave at frame or field rate for interlaced sources. A latest-frame buffer keeps preview work bounded independently of recording. The preview shows SD as 4:3 and uses BT.601 for SD or BT.709 for HD and larger sources. It retains the last frame and counts after stopping. Double-click the preview or press F11 to toggle full screen. The full-screen button appears after a short hover or when the preview is tapped. Press Esc to leave full screen.

The monitoring tabs show a luma waveform, RGB parade, chroma vectorscope, stereo frequency spectrum, and recent signal and timestamp events. The scopes and spectrum target 60 display updates per second. Progressive video contributes one measurement per input frame, and interlaced video contributes one per field when the capture format provides field order. The luma waveform labels the reference lines at 0, 7.5, and 100 IRE. In **Code** mode it labels the same lines at 16 / 32.4 / 235 for 8-bit and 64 / 129.7 / 940 for 10-bit. The spectrum shows the input sample rate in its header. The frequency axis extends to half that rate, from 0 to 24 kHz at 48 kHz. The left dBFS axis runs from 0 down to the level of one PCM count relative to full scale. Left and right meters report interval peaks in dBFS, cumulative full-scale clip counts, and phase correlation. The 32-bit clip indicator also catches the full-scale endpoint of 24-bit signals carried in a left-aligned 32-bit word. Missing video frames are estimated from SDK timestamp gaps relative to the selected mode. The footer counts no-input frames, suspected input drops, and frames skipped by the preview queue separately. Recording keeps received PCM in arrival order when audio timestamps disagree and reports the offsets and apparent gaps in QC. Video timestamps that reset or repeat are repaired using the mode’s nominal frame duration. QC flags missing tracks, suspected drops, and capture that ended early after saving the writable material. QC results provide links to the MOV and generated reports.

The stage shows video lock state. The Events view shows the number of lock losses and total time unlocked, with timestamps and recovery durations in the event list. The capture JSON keeps all unlocked intervals relative to recording start. Starting a clip with no lock contributes to time unlocked without counting as a loss of an established lock. Scopes show the share of luma pixels outside nominal video range (16–235 for 8-bit, 64–940 for 10-bit) on the latest analyzed frame. The percentage is a digital luma measurement and is unavailable without a readable input signal.

### Test signal without capture hardware

Open the source selector and choose **Test signal**. In the inspector choose **Normal signal** or **Timing faults**, set an output folder, then start preview and recording as usual. The source generates 720 × 486 interlaced NTSC 4:3 color bars with a frame counter and a 48 kHz stereo test tone. It records v210 and signed 16-bit PCM. Test filenames start with `TestSignal_` and the capture log identifies the built-in source.

The faults scenario repeats a skipped video frame, a short loss of signal, and an audio timestamp reset about every four seconds. Record at least eight seconds for a full cycle. Stopping runs the same MOV QC as DeckLink. The Windows CI tests both scenarios with exact frame and sample counts and verifies the generated reports with FFmpeg.

## Uno Desktop QC prototype

The standalone project in `src/UI/CaptureRack.Uno/` uses Uno Skia Desktop and the shared QC engine. Build or run it on Windows without changing the native WinUI 3 app or solution.

```powershell
dotnet run --project src/UI/CaptureRack.Uno/CaptureRack.Uno.csproj -c Release
```

Select **Open file…** and choose a recording.

- **MOV** runs the existing decode checks and saves the QC log, FrameMD5, and QCTools report beside the source. Use **View QC log** or **Open report folder** to inspect the files. MOV checks require FFmpeg with `cropdetect`. The app checks its own directory and PATH, plus the standard Homebrew binary directories on macOS.
- **TS, TS192, and M2T** run the same transport checks as `CaptureRack.Probe firewire analyze`. The app shows the packet layout, transport errors, timestamp counts, and first affected packet indexes. Use **Save JSON report…** to export full per-PID statistics. TS checks require no FFmpeg or capture device. Use a separate decode check to assess audio and video quality.

For a local Apple Silicon build, run this command on macOS with the .NET 11 SDK and Xcode installed.

```bash
dotnet publish src/UI/CaptureRack.Uno/CaptureRack.Uno.csproj -c Release -f net11.0-desktop -r osx-arm64 --self-contained -p:PackageFormat=app
```

The [macOS workflow](../../actions/workflows/uno-macos.yml?query=branch%3Amain+is%3Asuccess) publishes **CaptureRack-QC-macos-arm64** with `CaptureRack QC.app` inside. The CI bundle is for testing and has no Developer ID signature or notarization.

## Post-recording QC

After saving the MOV, CaptureRack runs QC in the background. Put `ffmpeg.exe` and `ffprobe.exe` from a recent FFmpeg build with the `cropdetect` filter on PATH or beside the executable. These tools read the saved file. Recording continues to use the DeckLink SDK directly.

For a recording named `tape.mov`, QC writes three files beside it.

- `tape.capture.json` records the input selections, capture counts and warnings, expected settings, and QC results.
- `tape.framemd5` contains MD5 hashes of the decoded video frames and audio blocks.
- `tape.mov.qctools.xml.gz` contains video signal statistics, crop detection, and audio statistics for QCTools.

QC checks that both tracks decode to the end and compares the decoded video frame and PCM sample counts with the recording counts. It also checks field order and PCM depth against the recording selections. The MOV remains available if QC fails or is canceled. Repeated checks use numbered filenames to preserve existing reports.

Use **Check MOV…** in the desktop app to run the same checks on an existing CaptureRack MOV, or use the probe command below. Offline QC works without a DeckLink card or its driver. On ARM64, use ARM64 builds of both FFmpeg tools. If the recording counts are unavailable, the command compares decoded counts against the MOV indexes.

```powershell
.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe qc 'D:\Captures\tape.mov'
```

## Command-line probe

Build and run from the repository root on a Windows machine with the .NET 11 SDK and Blackmagic Desktop Video installed.

```powershell
dotnet build src/CaptureRack.Probe -c Release
.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe list
.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe capture 0 10
```

`list` shows the current input routing and the 10-bit YUV modes available for it. `capture` takes a device index, a duration in seconds, and an optional mode code from `list` (for example, `ntsc` or `"pal "`). Without a mode, the probe uses the driver's detected mode or starts format detection with an available mode. Set video and audio input routing in Blackmagic Desktop Video Setup. The probe uses the existing routing and does not record to disk.

Capture reports valid video frames, no-input frames, PCM packets, audio peak, and frame and packet timestamps. Exit code `0` means the probe read both payloads and observed advancing timestamps without regressions. Exit code `2` means the stream ran but those conditions were not met. Exit code `1` indicates an invalid command or an SDK/setup error. A nonzero PCM peak confirms nonzero samples during the run. Use a known audio source to validate the selected physical input.

With no signal source, `capture 0 3 ntsc` can still exercise video and audio buffer access. The probe reports the first no-input video buffer separately and excludes it from the valid video frame count.
