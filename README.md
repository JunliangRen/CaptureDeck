# CaptureRack

CaptureRack is a .NET 11 video capture and preservation tool. The Windows desktop app uses native WinUI 3 and captures directly from the Blackmagic DeckLink API. The separate Uno Desktop prototype runs offline MOV and TS quality control on Windows and Apple Silicon Macs using the same QC engine.

## Builds

| Platform | Build | Features |
| --- | --- | --- |
| Windows x64 | [Native WinUI 3](../../actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess) | DeckLink, DirectShow, FireWire, Domesday Duplicator and RTL-SDR capture, real-time recording, and offline QC. |
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
|   |   `-- CaptureRack.Audio            # Shared WASAPI speaker output, PCM meters, gain and channel routing
|   |-- Processing/
|   |   |-- CaptureRack.Recording       # Shared recording, live encoding, and MKV recovery
|   |   `-- CaptureRack.QualityControl  # Capture validation and reports
|   |-- CaptureRack.Probe               # Command-line device probe and QC
|   `-- CaptureRack.DeviceControl       # Independent tape-transport contracts
`-- test/
    |-- CaptureRack.UT               # Windows capture and settings tests
    `-- CaptureRack.QualityControl.UT # Cross-platform TS analysis tests
```

Capture backends that call a capture-card vendor SDK live under `src/Capture/CaptureCards/`. SignalCapture keeps device-specific rules in `Devices/Domesday/` and `Devices/Sdr/RtlSdr/` and Windows USB transport in `Windows/`. DeckLink groups SDK bindings, capture, monitoring, and recording into `Interop/`, `Capture/`, `Monitoring/`, and `Recording/`.

FireWire / i.LINK and general DirectShow devices are separate capture sources. The Windows MSTape implementation lives under `CaptureRack.DirectShow/Windows/FireWire/MsTape/`. Reusable COM bindings and DirectShow media-type structures live in `CaptureRack.DirectShow/Windows/Interop/`. The general DirectShow probe uses the same COM bindings and managed sink.

### DirectShow camera controls

Expand **Camera controls** in the DirectShow inspector to read the selected device's settings. The panel uses the driver's legal values and automatic/manual modes, highlights unapplied modifications, and offers **Default** to restore driver defaults when reported. **Apply** writes the selected setting and reads the device back. With preview running, requests use the active capture filter. Camera changes are unavailable while recording.

```powershell
& $probe directshow list
& $probe directshow controls '<device-id>' --json
& $probe directshow controls '<device-id>' Focus auto
```

The controls report includes the interface, current value and mode, supported modes, and the driver's range, step and default where available. To investigate a new device, query `IAMCameraControl` and `IAMVideoProcAmp`, then call `GetRange` and `Get` for each documented property. Use `IKsControl` for documented KS properties such as auto-exposure priority (low-light compensation). For a vendor UVC Extension Unit, verify the USB device ID, the extension GUID, the control selector, the payload size and value mapping, and get/set support before enabling a setting. CaptureRack uses that path for 65°/78°/90° on Logitech BRIO USB `046d:085e`.

Under **Format details**, **Copy device diagnostics** copies a JSON report with the device model and installed driver version when Windows exposes them, the selected formats, camera controls, and the latest negotiation, sample and preview evidence for the current selection. Failures include the stage and HRESULT when available. Copying reads metadata and controls. Run **Test samples** separately to collect samples. The report omits the device instance path and does not include captured media.

Capture backends discover devices, negotiate formats, and receive samples. Downstream processing is organized by media format so multiple backends can use the same writer or analyzer. MSTape negotiates its supported packet layout and passes the original bytes to `Processing/CaptureRack.Recording/TransportStream/`. The writer handles buffering, byte-preserving output, and file finalization. It has no dependency on DirectShow or MSTape. `CaptureRack.QualityControl` provides the TS parser for both recording-time validation and offline file checks. Tape transport controls belong to `CaptureRack.DeviceControl`.

The shared recording project writes native TS and DV and provides real-time software encoding for video sources that supply frames or supported compressed video samples. The FireWire TS writer can be reused by future sources that deliver a compatible packet layout. CVBS/RF preview decoding is planned separately.

Capture and recording share the `CaptureSampleHandler` callback in `CaptureRack.Core`. DirectShow delivers bytes in the format negotiated at session start, with an optional stream timestamp and a discontinuity flag. Sample bytes are borrowed for the duration of the callback. A consumer that queues data copies it into its own buffer. The TS writer uses this callback for both MSTape and file replay in tests and retains its bounded queue and byte-preserving output. Other backends can adopt the contract with their own format negotiation.

**Available today.** DeckLink is the registered desktop capture backend. The desktop app supports DeckLink monitoring and MOV recording, plus FireWire MPEG-TS preview and recording on Windows x64. The command-line probe also captures FireWire MPEG-TS. The probe enumerates devices and checks input, and both entry points can run post-recording QC. Existing flat settings load as a DeckLink profile and move to the nested format on the next save.

**Possible future integrations.**

- **Other capture-card SDKs** can register alongside DeckLink using their own stable backend IDs. Each backend handles its own device discovery, format negotiation, and capture options.
- **General DirectShow capture** can support Windows cameras, video capture cards, and audio inputs that expose compatible filters.
- **FireWire** can preview and record MSTape MPEG-TS in the desktop app and capture TS from the command line. Native DV is a separate format.
- **Signal capture** can enumerate and probe Domesday Duplicator devices via WinUSB and inspect or configure RTL-SDR receivers via librtlsdr on Windows. MISRC support, raw sample recording, and live monitoring are future integrations.
- **Audio** provides shared-mode WASAPI speaker monitoring for all Windows capture sources and RF file playback. In Settings, select the current Windows default output or a specific playback device. An unavailable selected device is retried while recording continues.
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

## Windows RF recording

In **RF / Baseband SignalCapture**, select the receiver, choose the recording format and an output folder, then start recording. The waveform shows a 256-sample ADC trace. Min, max, peak-to-peak and RMS cover all samples between measurement updates. CLIP counts rail hits throughout the recording. RTL-SDR displays I and Q separately.

| Source | RAW output | Capture controls |
| --- | --- | --- |
| Domesday Duplicator | Signed little-endian 16-bit mono. Direct modes preserve the 10-bit ADC codes | Protocol-v1: 40/20 MSPS direct, 10/5 MSPS software-filtered from 20. Legacy: 40 MSPS direct, 20/10/5 from 40 |
| RTL-SDR Hi-Fi RF | Signed little-endian 16-bit mono, interpolated and mixed back to the tuned frequency at 8 MSPS | Selectable I/Q input rate (default 1.024 MSPS), manual tuner gain 0 dB, digital AGC off, live VGA 0–8 (default 8) on supported Blog V4 receivers |
| RTL-SDR I/Q | Native interleaved unsigned 8-bit I/Q pairs (.cu8) | Same gain and VGA controls, adjustable center frequency |

RTL-SDR frequency and input rate each have a preset menu and a custom entry in Hz. The initial tuning is 1.500 MHz (NTSC Hi-Fi). Native I/Q records at the selected input rate. Hi-Fi reconstruction produces 8 MSPS mono and requires the tuned input band to fit within 0–4 MHz. Select I/Q for FM and ADS-B tuning.

RF preview opens the selected receiver and monitors its ADC signal in RF-only mode or with a selected video decoder. The left meter shows positive and negative peaks about zero, one-second peak holds and separately resettable rail-hit indicators (ADC or I/Q). Studio Split puts the time-domain oscilloscope below the canvas; Scopes shows the decoded luma waveform or RGB parade alongside the vectorscope. Spectrum shows the RF FFT, with a PCM spectrum option for decoded audio. The capture inspector shows transport telemetry. Starting a recording from preview attaches a separate writer, and stopping it leaves the receiver and instruments running. DdD reports identify the shared preview session and the zero-based position of the clip's first sample in that session. Writer, clip and FIFO statistics belong to the individual recording; the first FIFO latch is its baseline. Direct recording from idle releases the receiver at stop.

DdD software rates use a streaming low-pass filter before decimation on the writer thread. ADC levels, clips and sequence checks use the hardware input. File naming, storage estimates and RF sample-rate tags use the output rate. The sidecar separates input and written sample counts, hardware and software decimation, and hardware and file rates. SW401 choices show nominal amplifier gain in descending order.

**RF preview audio.** Choose Betamax / Betamax Hi-Fi or LD in the preview toolbar to show the existing broadcast audio meter on the right. The decoder supplies the selected track name, PCM channel count and interleaved samples before speaker volume or mute. The meter waits for audio until the selected track supplies PCM, displays digital silence at −∞ dBFS, and clears peak holds and clip latches on lock loss or track replacement. Pure video and RF-only selections collapse the entire column. The decoder input and WinUI behavior have been verified with simulated mono and stereo PCM.

Domesday Duplicator offers RAW, native FLAC, and LDF FLAC (LD standard). LDF writes mono signed 16-bit RF samples losslessly as FLAC in an Ogg container with the historical `.ldf` extension. Select 40 MSPS for full-rate LaserDisc capture. The selector shows the full device name with Legacy or the identified firmware release.

Each DdD RAW, FLAC or LDF recording includes a `.fifo.json` sidecar. Its `Source` records the Windows USB instance, protocol, identified firmware release, FX3 and FPGA commits, output format, physical file sample rate, decimation, test mode and optional manually declared SW401 setting. Read switches 1 through 4 left to right (1 = ON). Leave the selector undeclared when the board setting is unknown. FLAC also carries the DDD sample-rate, decimation, test-mode and available version and front-end gain tags.

The RF inspector and footer estimate remaining DdD recording time on the selected output volume. RAW uses two bytes per sample and FLAC assumes 2:1 compression. A warning appears below ten estimated minutes. Actual FLAC duration depends on the signal.

FLAC uses FFmpeg and the MISRC-GUI RF rate convention (physical Hz divided by 1,000 in STREAMINFO). The filename and RF_SAMPLE_RATE_HZ tag carry the physical rate. RAW works independently of FFmpeg. Failed captures retain a partial file when data was written. The RTL-SDR DLL setup below applies to both the native app and Probe.

Enable **FPGA test ramp** in the DdD inspector to record a known pattern. CaptureRack checks the ADC codes and firmware sequence markers during capture. After an orderly stop it drains the outstanding WinUSB reads, finishes the writer, and releases the recorder for the next take. QC Center queues each closed file and verifies one at a time in the background, reading the entire RAW file or decoding the entire FLAC. Its session history shows queued, running, passed and failed checks. It checks the first code, every signed PCM sample, and the total against the live ramp before publishing the file. Test captures have `TestData_DdD` in the filename and a `.ramp.json` report. A short bulk read, exhaustion of the in-flight USB reads, a broken sequence, or failed verification ends the run with a partial file.

Protocol-v1 DdD recordings save a `.fifo.json` report beside the media on orderly shutdown or a handled capture error. CaptureRack uses one independent overlapped WinUSB control request to poll the FPGA FIFO every 250 ms. A slow reading stays in flight while bulk reads continue. Two cumulative failed or short USB control reads disable telemetry for the session while bulk capture continues. OverflowEvents and DroppedSamples count valid, distinct intervals after the baseline. ReadFailures, PollingDisabled, UnrecognizedReadings, MissedIntervals and CounterSaturated qualify the totals. Zero observed drops with no valid intervals provides no evidence of FIFO integrity. The RF measurement shows current FIFO words and depth, the largest interval peak after the baseline, and near-full samples accumulated in units of 256. The last reading and its timestamp remain available in the report. On a capture failure the same report includes the detected zero-based sample position (after startup discard), sequence marker context, USB operation and outstanding requests, FIFO snapshot, samples submitted to the writer and maximum pending write blocks and configured capacity. DdD allows 256 queued blocks (up to 64 MiB); the USB pool still holds 16 transfers. UsbCallbackMaxMs and UsbCallbackMeanMs measure wall time inside the bulk consumer, including validation, conversion, copy and enqueue. USB completion waits, telemetry and file I/O are outside this timer. Sequence validation remains active independently and a marker error ends recording. On a transfer failure, outstanding reads are cancelled individually and share a one-second completion deadline. A failed cancellation, unreaped request or failed hardware rollback locks the affected WinUSB interface path for the lifetime of the process. Any unreaped request retains its native handles, event and pinned storage. The error includes the original failure and cleanup diagnostics.

## Windows RF USB probe

```powershell
dotnet build src/CaptureRack.Probe -c Release
$probe = '.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe'
& $probe rf list
& $probe rf probe '<instance-id from rf list>' --json
```

For legacy DdD, WinUSB searches the primary and associated interfaces and their alternate settings for the bulk IN endpoint with the largest packet size. Equal sizes keep descriptor order. Capture selects the interface/alternate, configures test mode and clears the selected pipe's halt before posting reads. A changed alternate setting is restored after capture. Probe and the inspector indicate the link class inferred from the bulk packet size and warn about non-SuperSpeed links at 40 MSPS. Legacy marker diagnostics in the RF readout and report start unlocked, lock at the first observed modulo-64 advance, and end the capture on a discontinuity. After lock each marker must cover 65,536 samples. Constant marker bits from startup leave continuity unverified while ADC recording continues.

The probe targets Domesday Duplicator firmware through 3.2 using the legacy USB ID `1D50:603B` or the protocol-v1 runtime ID `1209:2347`. The WinUI RF selector and Probe identify official 3.1.0 and 3.2.0 builds by the FX3 commit in the USB product string. Probe also reports the FPGA commit independently. It identifies the protocol using the device descriptor and verifies the register map and application image for protocol-v1. A USB protocol version does not identify the physical board revision. Use the Windows WinUSB driver for the selected device. The probe reports the active driver and opens only the selected runtime instance.

Probing reads USB descriptors, the stream endpoint, and protocol-v1 firmware identity. It does not start collection, write device registers, rebind drivers, or enter bootloader mode. Device capture and preview are outside this probe. See the [Domesday Duplicator Windows setup guide](https://domesday86.github.io/DomesdayDuplicator/capture-gui/install-msi/) for driver binding.

## Windows RTL-SDR probe

Windows x64 RF preview uses the upstream `VHSDecode.Preview.Realtime` package, version `2.33.0-preview.ci.21`. Configure the decoder repository owner's GitHub Packages NuGet source with package read access. ARM64 builds omit the realtime decoder.

Preview supports NTSC/PAL VHS, S-VHS, CVBS and standard Betamax at 20 or 40 MSPS. SECAM VHS and MESECAM use 625/50 timing. NTSC Betamax also offers Hi-Fi, SuperBeta, SuperBeta Hi-Fi and ED Beta profiles. LaserDisc offers video alone, analog stereo or 44.1 kHz digital PCM in PAL and NTSC, plus DTS and AC-3 RF in NTSC. Separate VHS Hi-Fi audio accepts 8, 8.192, 10, 20 and 40 MSPS mono RF. Decoder options include the thread budget (1–64) and AC-3 RF presentation delay (128 ms by default). Decoder telemetry shows horizontal, vertical, color and selected audio lock. The RF oscilloscope can show Y (±120 IRE), U or V (±1 normalized unit) from the central scanline of the selected field and crop, before RGB clipping. Paused file playback retains frame/field inspection. ADC and tape head-switch traces use CaptureRack's input samples.

In the RF preview toolbar, choose VHS, then the television system and tape format. NTSC and PAL offer VHS, S-VHS and Hi-Fi. Select Hi-Fi to monitor audio from a Hi-Fi RF input. Betamax profiles with audio retain their monitoring toggle in Decoder options. SECAM and MESECAM offer VHS. In the RF oscilloscope source menu, select RF FFT to view the input spectrum with a linear frequency axis and a 0 to −120 dBFS scale. Mono RF spans DC to half the sample rate. Complex I/Q spans negative to positive half-rate relative to the tuned frequency.

Windows builds and publishes of App and Probe include the matching RTL-SDR and libusb DLLs, with Blog V4 VGA support. Bind the receiver to WinUSB. An absolute `CAPTURERACK_RTLSDR_LIBRARY` path can override the bundled library. The accompanying licenses and corresponding source are in `licenses/rtl-sdr/`.

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

## Real-time software recording output

Under **Output → Recording output**, choose how to save DeckLink, DirectShow or the built-in test signal. **Device original · direct copy** is the default for new installations and older settings without a saved choice. DeckLink saves uncompressed video to MOV, and DirectShow can change the container while preserving the captured video payload. An explicit software choice is remembered. Software output encodes during capture and saves MKV with `Encoded_` and the codec in the filename. FireWire uses its native DV or TS writer.

- **FFV1 + FLAC 8** stores video in FFV1 v3 with slice CRC and encodes PCM as FLAC compression level 8. It retains the captured video bit depth and scan order on the tested 10-bit 4:2:2 path. 8-bit PCM expands to 16-bit FLAC. 32-bit FLAC requires an FFmpeg build with support for the encoder’s 32-bit mode.
- **H.264, H.265, AV1 and MPEG-2** offer target video bitrate and codec level plus explicit software deinterlacing. They produce 8-bit 4:2:0 video with PCM audio. For interlaced sources, enable software deinterlacing before recording H.265 or AV1.
- Software output requires a local FFmpeg build with the selected encoder. H.264, H.265 and AV1 use `libx264`, `libx265` and `libsvtav1`, respectively. Encoding and disk writing run independently from preview. Real-time throughput depends on the input, CPU and disk.

On a clean stop, CaptureRack drains the queue, writes the seek index, and publishes the MKV and its `.encoding.json` report. If the queue fills or the software encoder exits early, the recording stops and preview can continue. CaptureRack keeps the `.partial.mkv` and records the failure reason in its checkpoint when available.

The encoding report and the live checkpoint record the same capture diagnostics.

| Fields | How to read them |
| --- | --- |
| `captureVideoPackets`, `captureEstimatedMissingVideoFrames`, `captureVideoDiscontinuities` | A gap in source timestamps is evidence of frames missing before encoding. The estimated count is null when no usable consecutive timestamps are available. Missing, repeated and regressing timestamps have separate counters. |
| `queuePeakPackets`, `queuePeakBytes`, `queueRejectedPackets`, `queueRejectedVideoPackets` | Peak queued audio and video packets and their payload bytes, excluding the packet already held by the encoder writer. DirectShow reserves memory for that packet until the write ends. A rejection means the queue could not accept a sample. |
| `videoFramesToEncoder`, `decodedVideoFrames` | Compare complete frames sent to FFmpeg with frames decoded from the saved MKV. DirectShow MPEG-2 uses `videoPacketsToEncoder` because a sample can contain partial or multiple pictures. QC fills the decoded count after a successful full decode, with no second decode. The first completed decode is retained across QC reruns. Until then the count is null and `decodeStatus` records progress or failure. |
| `longestEncoderWriteMilliseconds`, `longestDiskStallMilliseconds` | Compare the longest FFmpeg input write with the longer of the media write and durable flush peaks. A slow disk can also block FFmpeg input. Idle waits for encoded output are excluded. |
| `lastCompleteClusterTimestampMilliseconds`, `completeClusterBytes` | Start timestamp of the last structurally complete Cluster and the byte position just after it, measured at the latest successful media checkpoint. On a handled write failure CaptureRack tries to checkpoint acknowledged writes through the last complete Cluster. Recovery also checks the available CRCs. |

To recover an interrupted CaptureRack software recording, choose a new destination:

```powershell
.\Probe\CaptureRack.Probe.exe recover-mkv '.\capture.mkv.partial.mkv' '.\capture-recovered.mkv'
```

Recovery checks MKV element boundaries and available CRCs, copies the intact prefix up to the first damaged or incomplete cluster, and rebuilds seek entries from the available keyframes. It preserves the original file and does not re-encode the recovered media.

## Windows x64 FireWire / i.LINK

Choose **FireWire / i.LINK** from the source selector, select a device in the right capture panel, and use the shared bottom bar to start preview. Start playback on the deck. FFmpeg decodes the monitoring copy for the common preview, Bob-Weave, scopes, meters, and optional speaker monitoring. Choose an output folder and record after data arrives. MSDV devices can supply consumer DV25 (including MiniDV in DV mode), DVCPRO 25, DVCPRO50, or DVCPRO HD when their drivers expose the native format. CaptureRack saves them as raw `.dv` with a format tag before the timestamp. HDV and D-VHS MPEG-TS are saved as `.ts`, with `iLINK188` or `iLINK192` before the timestamp. The writer preserves the driver payload, including all embedded audio, metadata, and any four-byte TS packet prefix. Hardware recording can run with FFmpeg unavailable. Stop recording to run structural QC and open its result from the bottom bar.

The i.LINK inspector shows the tape counter and deck state when the driver returns them. For DV it reads the profile, frame size and rate, embedded timecode, recording date/time, audio sampling rate, quantization and channel count, aspect ratio, and DIF frame statistics. DV audio monitoring uses the first stereo pair. MPEG-TS shows programs, tracks, available broadcast metadata and per-PID statistics. Preview re-probes when playback resumes after a gap or stream content changes. The copy button includes the live diagnostics.

Without a deck, choose a **Simulated** device from the same i.LINK device list. The inputs cover MiniDV, DVCPRO 25, and DVCPRO50 in NTSC/PAL, DVCPRO HD in 1080i50/59.94 and 720p50/59.94, HDV 720p TS188, and HDV 1080i TS188/TS192. FFmpeg generates a short test clip for the selected format, and an in-process DirectShow source loops the original encoded bytes at capture speed. The simulator exercises media negotiation and sample delivery inside CaptureRack. Physical 1394 discovery and AV/C tape commands require hardware.

### Command-line probe

The .NET 11 probe includes a FireWire command group. It uses MSDV/MSTape through DirectShow on Windows x64 and analyzes `.dv` and TS files offline.

```powershell
dotnet build src/CaptureRack.Probe -c Release
$probe = '.\src\CaptureRack.Probe\bin\Release\net11.0-windows\win-x64\CaptureRack.Probe.exe'
& $probe firewire list
& $probe firewire probe --device '<device-id>'
& $probe firewire capture --device '<device-id>' --output 'D:\Captures\iLINK192_tape.ts' --format ts192
& $probe firewire capture --device '<dv-device-id>' --output 'D:\Captures\iLINK_DV_tape.dv' --format dv
& $probe firewire analyze --input 'D:\Captures\iLINK192_tape.ts' --json
```

Copy the device ID from `list`. Use an existing output directory. Once the graph starts, play the tape manually. Ctrl+C stops PC capture, drains queued samples, and finalizes the file. `--duration` limits capture from the first sample. `--no-data-timeout` sets the idle timeout in seconds (default 30).

- Live capture accepts MSDV interleaved `dv`, `dv25` (DVCPRO 25), `dv50`, and `dvh1` (DVCPRO HD), plus MSTape `ts188` and `ts192`. Omitting `--format` chooses the native DV output or prefers TS192 when the driver offers both TS layouts.
- Files contain the bytes delivered by the capture driver, including the four-byte prefix in the 192-byte layout. Capture writes to `<output>.partial` before finalization and retains the partial file on failure. Existing files are protected from overwrites.
- The sidecar `<output>.report.json` records file and queue accounting, transport sync, malformed packets, TEI, continuity, PCR and PTS statistics. It includes zero-based packet indexes for the first error of each reported kind. PCR checks account for wraparound and signaled discontinuities. The report covers transport integrity, and separate playback is needed to verify decoding.
- `analyze` inspects an existing recording and prints a summary or JSON with `--json`. It recognizes raw DV by `.dv`, or auto-detects a TS layout with at least five valid aligned packets at the beginning. Use `--format dv` to analyze raw DV under another extension. `dv25`, `dv50`, and `dvh1` check for the requested profile. A raw NTSC 25 Mb/s file can require the original device subtype or the CaptureRack filename to distinguish DVCPRO 25 from consumer DV. DV reports include frame counts, structural errors, and the first and last frame metadata. TS reports cover sync, TEI, continuity, and backward PCR. Structural errors, profile mismatches and a truncated tail return exit code 4.

The .NET tests use the app's selectable simulations for DirectShow negotiation, FFmpeg A/V preview, live metadata, byte-identical recording and offline QC. A VICTOR HM-DHX2 previously verified live TS192 capture. Physical MiniDV, HDV, and DVCPRO capture remain unverified.

## Native Windows desktop app

Build and launch from the repository root with the .NET 11 SDK. Install Blackmagic Desktop Video on x64 to capture from DeckLink.

```powershell
dotnet build CaptureRack.slnx -c Release
.\src\UI\CaptureRack.App\bin\Release\net11.0-windows10.0.26100.0\win-x64\CaptureRack.App.exe
```

Select a device, video source, audio source, and video format. The source lists come from the DeckLink SDK and the mode list follows the selected video source and format. **Start preview** monitors the input. To record, start preview and choose an existing output folder. With Auto detect, recording is available when the card delivers readable frames in the active preview mode. Without a signal, the app labels that mode as a fallback and allows recording no-input frames. An input mode change during an Auto detect recording ends the clip in its original mode, runs QC, and lets preview follow the new mode. **Start recording** names the MOV with the local start time and selected formats, for example `CaptureRack_20261124_060236_633_10bit-Uncompressed_16bit-PCM.mov`. **Stop recording** finalizes the MOV and runs QC while preview continues. The app applies the selected routing for the session and restores the previous routing on stop.

Open **Settings** from the gear button in the title bar. CaptureRack remembers the active capture backend, DeckLink routing/formats/analog/VANC options, DirectShow device/format, FireWire device, test-signal scenario, output folder, tape label, and UI preferences in `%LOCALAPPDATA%\CaptureRack\settings.json`. Startup restores the selections and waits for you to start preview. The first launch uses the Windows display language, with English as the fallback, and saves the resolved selection. Later launches use the saved app language. Use the fixed back button, the mouse back button, or Alt+Left to return from Settings. Keyboard shortcuts cover `F5` (preview), `F9` (record), `F11` / `Esc` (full screen), `Ctrl+I` (capture panel), `Ctrl+1`–`5` (Focus / Split / Scopes / Spectrum / Events), and `M` (toggle local audio monitor). The bottom bar keeps preview and recording controls together, shows a recording indicator, elapsed time, and inline finalization/container status while writing, and surfaces backend-specific live telemetry (frame/packet counts, bitrate, continuity/sync errors, unreadable frames, and preview skips). Open the compact status button for signal details and the active filename. The storage control shows free space and estimated recording time from the selected mode and PCM format. The estimate excludes MOV and QC report overhead and depends on the free space available when it was checked.

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

In the RF device list, select **File playback**, open a mono RF (.s16, .raw, .flac, .ldf) or I/Q (.cu8, stereo FLAC) recording, then press Play. Drag the timeline to seek or press Pause to resume later. Check the RF sample rate for headerless files. The recording controls are disabled while this source is selected.

RF display settings include Active video or Full raster, original interlaced frames or motion-adaptive Bob-Weave x1/x2, display aspect, safe areas and NTSC 480-line guides. Changing the decoder while previewing keeps the input session open.

For RTL-SDR Hi-Fi RF playback, open the reconstructed mono FLAC or signed 16-bit RAW file in the RF file player. Recognized Hi-Fi captures select VHS Hi-Fi automatically. Select NTSC or PAL to match the recording. Playback feeds 48 kHz stereo to the audio meter, PCM spectrum and local speaker monitor. The speaker button or M mutes monitoring independently of the meter. Native I/Q files stay in the RF monitor.

## Validation and hardware support

Run from the repository root with Python 3.11+, the repository SDK and FFmpeg/FFprobe on PATH. Use a new output directory per run. Each contains `validation.json` with the candidate version and commit, evidence source, elapsed time, outcome and first failed stage. Compare the same profile across checkouts. Short runs also retain individual test results and capture/queue measurements. The recovery profile kills its own test-signal capture process after the first durable cluster, recovers the file and fully decodes both tracks.

```powershell
python tools/validate.py short bin/check-short
dotnet publish src/CaptureRack.Probe -c Release -r win-x64 -o bin/check-probe
python tools/validate.py recovery bin/check-recovery --probe bin/check-probe/CaptureRack.Probe.exe
python tools/validate.py file bin/check-file --probe bin/check-probe/CaptureRack.Probe.exe --media example.mov
```

With a Domesday Duplicator 3.2 connected, obtain its instance ID with `CaptureRack.Probe rf list`. Run `python tools/validate.py ddd bin/check-ramp --probe bin/check-probe/CaptureRack.Probe.exe --device "<instance-id>"` for the 600-second, 40 MSPS FLAC baseline. Review the live and verified sample counts, FIFO coverage, queue peak and available disk before extending to `--seconds 28800`. The 2:1 disk estimate is 24 GB for ten minutes and 1.152 TB for eight hours. Actual compression varies. For A/V clock drift, feed stable audio and video to a DeckLink input and run `python tools/validate.py av-clock bin/check-clocks --probe bin/check-probe/CaptureRack.Probe.exe --device 0 --seconds 28800`. This profile measures change in A/V timestamp offset in 1/48000-second ticks. It stores clock observations without writing a long capture file.

| Source and mode | Platform | Evidence |
| --- | --- | --- |
| Domesday Duplicator 3.2, 40 MSPS ramp FLAC | Windows x64 | Physical ten-minute capture and per-sample decode, plus consecutive clips in one receiver session |
| Domesday Duplicator Legacy / 3.1 | Windows x64 | Legacy marker and transport fault simulation. Physical validation pending for both revisions |
| RTL-SDR Blog V4, I/Q and mono Hi-Fi | Windows x64 | Physical short RAW/FLAC captures and VGA adjustment. Recorded Hi-Fi files replayed |
| DeckLink and DirectShow | Windows x64 | Physical input and preview tests, simulated file counts and encoder faults. Eight-hour A/V drift baseline pending |
| i.LINK DV and HDV / D-VHS | Windows x64 | HM-DHX2 physical TS192 capture. DV/HDV format coverage from simulated sources |
| Offline QC | Windows x64 / ARM64, macOS ARM64 Uno | Windows unit and file-decode tests, macOS portable tests and package verification. ARM64 live hardware validation is separate |

[Versioned hardware and engineering evidence through v0.7.0](docs/validation/through-v0.7.0.md)

### Release candidates

Dispatch **Verify release candidate** at the desired ref. Both Windows builds and the macOS bundle come from the same workflow SHA. The final artifact contains three verified ZIPs and `SHA256SUMS.txt`. For downloaded Actions artifacts, run `python tools/release.py NEW_DIRECTORY --artifacts ARTIFACT_DIRECTORY --version VERSION --commit FULL_SHA`. Omit `--artifacts` to verify existing ZIPs. Update the realtime package content hashes from the official NuGet archive when its version changes. Windows CI pins the FFmpeg release asset and digest independently for each architecture.
