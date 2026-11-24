# CaptureDeck

CaptureDeck is a .NET 11 video capture and preservation tool. The Windows desktop app uses native WinUI 3 and captures directly from the Blackmagic DeckLink API. The separate Uno Desktop prototype runs offline MOV and TS quality control on Windows and Apple Silicon Macs using the same QC engine.

## Builds

| Platform | Build | Features |
| --- | --- | --- |
| Windows x64 | [Native WinUI 3](../../actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess) | DeckLink preview, recording, and offline QC. FireWire TS capture from the command line. |
| Windows ARM64 | [Native WinUI 3](../../actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess) | Offline QC. DeckLink capture is unavailable. |
| macOS ARM64 | [Uno Desktop prototype](../../actions/workflows/uno-macos.yml?query=branch%3Amain+is%3Asuccess) | Offline MOV and TS QC, report viewing, and TS JSON export |

Open a successful run and download the matching package from **Artifacts**. The Windows artifacts contain the app and managed command-line probe. Run `Probe/CaptureDeck.Probe.exe firewire` to access the FireWire commands. The macOS artifact contains the Apple Silicon test app.

## Project structure

```text
CaptureDeck/
|-- src/
|   |-- Capture/
|   |   |-- CaptureDeck.Core         # Shared capture contracts and backend registry
|   |   |-- CaptureDeck.DeckLink     # Blackmagic DeckLink capture, preview, and MOV recording
|   |   |-- CaptureDeck.DirectShow   # Windows DirectShow capture and shared interop
|   |   |-- CaptureDeck.SignalCapture # Sampled-signal capture devices and diagnostics
|   |   `-- CaptureDeck.Audio        # Reserved for standalone audio capture
|   |-- Processing/
|   |   `-- CaptureDeck.Recording    # Shared transport-stream recording
|   |-- CaptureDeck.App              # Native WinUI desktop app
|   |-- CaptureDeck.Uno              # Uno Desktop offline QC prototype
|   |-- CaptureDeck.Probe            # Command-line device probe and QC
|   |-- CaptureDeck.DeviceControl    # Independent tape-transport contracts
|   `-- CaptureDeck.QualityControl   # Post-recording validation and reports
`-- test/
    |-- CaptureDeck.UT               # Windows capture and settings tests
    `-- CaptureDeck.QualityControl.UT # Cross-platform TS analysis tests
```

Within the capture projects, SignalCapture keeps device-specific rules in `Devices/Domesday/` and `Devices/Sdr/RtlSdr/` and Windows USB transport in `Windows/`. DeckLink groups SDK bindings, capture, monitoring, and recording into `Interop/`, `Capture/`, `Monitoring/`, and `Recording/`.

FireWire / i.LINK and general DirectShow devices are separate capture sources. The Windows MSTape implementation lives under `CaptureDeck.DirectShow/Windows/FireWire/MsTape/`. Reusable COM bindings and DirectShow media-type structures live in `CaptureDeck.DirectShow/Windows/Interop/`. General DirectShow device capture can use those bindings when it is added.

Capture backends discover devices, negotiate formats, and receive samples. Downstream processing is organized by media format so multiple backends can use the same writer or analyzer. MSTape negotiates its supported packet layout and passes the original bytes to `Processing/CaptureDeck.Recording/TransportStream/`. The writer handles buffering, byte-preserving output, and file finalization. It has no dependency on DirectShow or MSTape. `CaptureDeck.QualityControl` provides the TS parser for both recording-time validation and offline file checks. Tape transport controls belong to `CaptureDeck.DeviceControl`.

The shared recording project currently contains the Windows TS writer. Future sources that deliver TS can use it with their own packet layout. DV handling, shared frame and PCM processing, and CVBS/RF preview decoding can be added as separate paths when their sources are connected.

**Available today.** DeckLink is the registered desktop capture backend. The desktop app uses DeckLink for live monitoring and MOV recording. The command-line probe also captures FireWire MPEG-TS on Windows x64. The probe enumerates devices and checks input, and both entry points can run post-recording QC. Existing flat settings load as a DeckLink profile and move to the nested format on the next save.

**Possible future integrations.**

- **Other capture-card SDKs** can register alongside DeckLink using their own stable backend IDs. Each backend handles its own device discovery, format negotiation, and capture options.
- **General DirectShow capture** can support Windows cameras, video capture cards, and audio inputs that expose compatible filters.
- **FireWire** can probe MSTape MPEG-TS sources from the command line. Native DV and a decoded preview remain future integrations.
- **Signal capture** can enumerate and probe Domesday Duplicator devices via WinUSB and inspect or configure RTL-SDR receivers via librtlsdr on Windows. MISRC support, raw sample recording, and live monitoring are future integrations.
- **Audio** is reserved for standalone audio inputs such as ASIO and WASAPI.
- **Device control** is independent of the capture source. The transport contract is ready for future AV/C, JVC JLIP, and other RS-232 protocols. It distinguishes command acceptance from observed device state. Recording to the computer and recording to tape are separate operations.

Run the unit tests from the repository root.

```powershell
dotnet test test/CaptureDeck.UT/CaptureDeck.UT.csproj -c Release
dotnet test test/CaptureDeck.QualityControl.UT/CaptureDeck.QualityControl.UT.csproj -c Release
```

## Windows builds

The default build targets Windows x64 (Intel/AMD). Publish the app and probe for the architecture that will run them.

```powershell
$rid = 'win-arm64' # Or 'win-x64'
dotnet publish src/CaptureDeck.App -c Release -r $rid --self-contained
dotnet publish src/CaptureDeck.Probe -c Release -r $rid --self-contained
```

On Windows ARM64, CaptureDeck can open settings and check existing MOV files. DeckLink capture is currently available on Windows x64 with a compatible Blackmagic Desktop Video installation. Device support for future backends is validated separately.

## Windows RF USB probe

```powershell
dotnet build src/CaptureDeck.Probe -c Release
$probe = '.\src\CaptureDeck.Probe\bin\Release\net11.0-windows\win-x64\CaptureDeck.Probe.exe'
& $probe rf list
& $probe rf probe '<instance-id from rf list>' --json
```

The probe targets Domesday Duplicator firmware through 3.2 using the legacy USB ID `1D50:603B` or the protocol-v1 runtime ID `1209:2347`. It identifies the protocol using the device descriptor and verifies the register map and application image for protocol-v1. A USB protocol version does not identify the physical board revision. Use the Windows WinUSB driver for the selected device. The probe reports the active driver and opens only the selected runtime instance.

Probing reads USB descriptors, the stream endpoint, and protocol-v1 firmware identity. It does not start collection, write device registers, rebind drivers, or enter bootloader mode. Device capture and preview are outside this probe. See the [Domesday Duplicator Windows setup guide](https://domesday86.github.io/DomesdayDuplicator/capture-gui/install-msi/) for driver binding.

## Windows RTL-SDR probe

RTL-SDR uses a matching-architecture `rtlsdr.dll` or `librtlsdr.dll` beside the probe, or an absolute path set in `CAPTUREDECK_RTLSDR_LIBRARY`. Place its native dependencies next to the library and bind the receiver to WinUSB. This optional library is supplied separately.

```powershell
& $probe rf sdr rtl-sdr list
& $probe rf sdr rtl-sdr probe 0
& $probe rf sdr rtl-sdr configure 0 --frequency 1500000 --sample-rate 1024000 --gain 0 --agc off --vga 8 --json
```

`probe` shows the tuner and its supported manual gain values. `configure` applies the settings for a diagnostic session and closes the receiver without collecting I/Q. Defaults are 100 MHz, 2.4 MSPS, auto tuner gain, and digital AGC off. Set manual gain in tenths of a dB (for example, 197 means 19.7 dB). Frequency correction is optional (`--ppm`). Manual VGA indices 0–8 are enabled only for the R828D-based RTL-SDR Blog V4 with the patched librtlsdr exports used by [MISRC-GUI](https://github.com/harrypm/MISRC-GUI/blob/main/docs/RTL-SDR.md). The manual VGA default is 8 when supported.

## Windows x64 FireWire / i.LINK probe

The .NET 11 probe includes a FireWire command group. It calls MSTape/DirectShow on Windows x64 and can analyze TS files without a deck.

```powershell
dotnet build src/CaptureDeck.Probe -c Release
$probe = '.\src\CaptureDeck.Probe\bin\Release\net11.0-windows\win-x64\CaptureDeck.Probe.exe'
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

The .NET tests use generated packets and an in-process DirectShow source. Live capture on the HM-DHX2 is pending.

## Native Windows desktop app

Build and launch from the repository root with the .NET 11 SDK. Install Blackmagic Desktop Video on x64 to capture from DeckLink.

```powershell
dotnet build CaptureDeck.slnx -c Release
.\src\CaptureDeck.App\bin\Release\net11.0-windows10.0.26100.0\win-x64\CaptureDeck.App.exe
```

Select a device, video source, audio source, and video format. The source lists come from the DeckLink SDK and the mode list follows the selected video source and format. **Start preview** monitors the input. To record, select a fixed input mode, start preview, and choose an existing output folder. **Start recording** names the MOV with the local start time and selected formats, for example `CaptureDeck_20261124_060236_633_10bit-Uncompressed_16bit-PCM.mov`. **Stop recording** finalizes the MOV and runs QC while preview continues. The app applies the selected routing for the session and restores the previous routing on stop.

Open **Settings** from the gear button or the configuration summary in the bottom bar. CaptureDeck remembers the selected device, inputs, mode, formats, output folder, and settings language in `%LOCALAPPDATA%\CaptureDeck\settings.json`. Startup restores the selections and waits for you to start preview. Settings follow the Windows display language by default, with English as the fallback. Choose a specific settings language to override the Windows preference. The bottom bar displays free space and estimates recording time from the selected video mode and PCM format. The estimate excludes MOV and QC report overhead and depends on the free space available when it was checked.

The two MOV video options request uncompressed 10-bit 4:2:2 (`v210`, default) or 8-bit 4:2:2 (UYVY, `2vuy`) directly from the SDK. **Audio format** selects native 48 kHz stereo signed PCM, either 16-bit (default) or 32-bit. Preview requests 32-bit PCM when supported, so its spectrum uses the SDK input sample depth. A 16-bit recording from 32-bit input stores the high signed 16 bits of each sample. Matching input and recording depths are written directly. The MOV carries the selected mode's frame rate and interlaced field order. Readable no-input frames from the card are included in the recording and counted separately from valid video.

The native WinUI preview converts either format to BGRA. A latest-frame buffer keeps preview work bounded independently of recording. The preview shows SD as 4:3 and uses BT.601 for SD or BT.709 for HD and larger sources. It retains the last frame and counts after stopping.

The monitoring tabs show a luma waveform, chroma vectorscope, stereo frequency spectrum, and recent signal and timestamp events. The spectrum extends to half the SDK input sample rate and uses the level of one PCM count relative to full scale as its display floor. Left and right meters report interval peaks in dBFS, cumulative full-scale clip counts, and phase correlation. The 32-bit clip indicator also catches the full-scale endpoint of 24-bit signals carried in a left-aligned 32-bit word. Missing video frames are estimated from SDK timestamp gaps relative to the selected mode. No-input frames and preview skips are counted separately from those gaps.

## Uno Desktop QC prototype

The standalone project in `src/CaptureDeck.Uno/` uses Uno Skia Desktop and the shared QC engine. Build or run it on Windows without changing the native WinUI 3 app or solution.

```powershell
dotnet run --project src/CaptureDeck.Uno/CaptureDeck.Uno.csproj -c Release
```

Select **Open file…** and choose a recording.

- **MOV** runs the existing decode checks and saves the QC log, FrameMD5, and QCTools report beside the source. Use **View QC log** or **Open report folder** to inspect the files. MOV checks require FFmpeg with `cropdetect`. The app checks its own directory and PATH, plus the standard Homebrew binary directories on macOS.
- **TS, TS192, and M2T** run the same transport checks as `CaptureDeck.Probe firewire analyze`. The app shows the packet layout, transport errors, timestamp counts, and first affected packet indexes. Use **Save JSON report…** to export full per-PID statistics. TS checks require no FFmpeg or capture device. Use a separate decode check to assess audio and video quality.

For a local Apple Silicon build, run this command on macOS with the .NET 11 SDK and Xcode installed.

```bash
dotnet publish src/CaptureDeck.Uno/CaptureDeck.Uno.csproj -c Release -f net11.0-desktop -r osx-arm64 --self-contained -p:PackageFormat=app
```

The [macOS workflow](../../actions/workflows/uno-macos.yml?query=branch%3Amain+is%3Asuccess) publishes **CaptureDeck-QC-macos-arm64** with `CaptureDeck QC.app` inside. The CI bundle is for testing and has no Developer ID signature or notarization.

## Post-recording QC

After saving the MOV, CaptureDeck runs QC in the background. Put `ffmpeg.exe` and `ffprobe.exe` from a recent FFmpeg build with the `cropdetect` filter on PATH or beside the executable. These tools read the saved file. Recording continues to use the DeckLink SDK directly.

For a recording named `tape.mov`, QC writes three files beside it.

- `tape.capture.json` records the input selections, capture counts and warnings, expected settings, and QC results.
- `tape.framemd5` contains MD5 hashes of the decoded video frames and audio blocks.
- `tape.mov.qctools.xml.gz` contains video signal statistics, crop detection, and audio statistics for QCTools.

QC checks that both tracks decode to the end and compares the decoded video frame and PCM sample counts with the recording counts. It also checks field order and PCM depth against the recording selections. The MOV remains available if QC fails or is canceled. Repeated checks use numbered filenames to preserve existing reports.

Use **Check MOV…** in the desktop app to run the same checks on an existing CaptureDeck MOV, or use the probe command below. Offline QC works without a DeckLink card or its driver. On ARM64, use ARM64 builds of both FFmpeg tools. If the recording counts are unavailable, the command compares decoded counts against the MOV indexes.

```powershell
.\src\CaptureDeck.Probe\bin\Release\net11.0-windows\win-x64\CaptureDeck.Probe.exe qc 'D:\Captures\tape.mov'
```

## Command-line probe

Build and run from the repository root on a Windows machine with the .NET 11 SDK and Blackmagic Desktop Video installed.

```powershell
dotnet build src/CaptureDeck.Probe -c Release
.\src\CaptureDeck.Probe\bin\Release\net11.0-windows\win-x64\CaptureDeck.Probe.exe list
.\src\CaptureDeck.Probe\bin\Release\net11.0-windows\win-x64\CaptureDeck.Probe.exe capture 0 10
```

`list` shows the current input routing and the 10-bit YUV modes available for it. `capture` takes a device index, a duration in seconds, and an optional mode code from `list` (for example, `ntsc` or `"pal "`). Without a mode, the probe uses the driver's detected mode or starts format detection with an available mode. Set video and audio input routing in Blackmagic Desktop Video Setup. The probe uses the existing routing and does not record to disk.

Capture reports valid video frames, no-input frames, PCM packets, audio peak, and frame and packet timestamps. Exit code `0` means the probe read both payloads and observed advancing timestamps without regressions. Exit code `2` means the stream ran but those conditions were not met. Exit code `1` indicates an invalid command or an SDK/setup error. A nonzero PCM peak confirms nonzero samples during the run. Use a known audio source to validate the selected physical input.

With no signal source, `capture 0 3 ntsc` can still exercise video and audio buffer access. The probe reports the first no-input video buffer separately and excludes it from the valid video frame count.
