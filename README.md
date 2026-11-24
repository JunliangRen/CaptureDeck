# CaptureDeck

Windows video capture and preservation. CaptureDeck uses .NET 11 and native WinUI 3 (Windows App SDK 2.7.0). The app and the command-line probe share the same capture module, which calls Blackmagic Desktop Video directly through the Windows COM interfaces from DeckLink SDK 16.0.

## Project structure

```text
CaptureDeck/
|-- src/
|   |-- Capture/
|   |   |-- CaptureDeck.Core         # Shared capture contracts and backend registry
|   |   |-- CaptureDeck.DeckLink     # Blackmagic DeckLink capture, preview, and MOV recording
|   |   |-- CaptureDeck.DirectShow   # Reserved for Windows capture filters
|   |   |-- CaptureDeck.FireWire     # Reserved for IEEE 1394 / i.LINK capture
|   |   |-- CaptureDeck.RF           # Reserved for raw RF acquisition
|   |   `-- CaptureDeck.Audio        # Reserved for standalone audio capture
|   |-- CaptureDeck.App              # Native WinUI desktop app
|   |-- CaptureDeck.Probe            # Command-line device probe and QC
|   |-- CaptureDeck.DeviceControl    # Independent tape-transport contracts
|   `-- CaptureDeck.QualityControl   # Post-recording validation and reports
`-- UT/
    `-- CaptureDeck.UT               # Hardware-independent unit tests
```

**Available today.** DeckLink is the only registered capture backend. The desktop app uses it for live monitoring and MOV recording. The probe enumerates devices and checks input, and both entry points can run post-recording QC. Existing flat settings load as a DeckLink profile and move to the nested format on the next save.

**Possible future integrations.**

- **Other capture-card SDKs** can register alongside DeckLink using their own stable backend IDs. Each backend handles its own device discovery, format negotiation, and capture options.
- **DirectShow** can support Windows capture devices that expose compatible filters.
- **FireWire** is reserved for i.LINK MPEG-TS from D-VHS and compatible HDV devices, plus native DV. The intended recording path preserves the source stream while a separate path decodes it for preview.
- **RF** is reserved for devices such as Domesday Duplicator and MISRC, with raw sample recording and live signal monitoring.
- **Audio** is reserved for standalone audio inputs such as ASIO and WASAPI.
- **Device control** is independent of the capture source. The transport contract is ready for future AV/C, JVC JLIP, and other RS-232 protocols. It distinguishes command acceptance from observed device state. Recording to the computer and recording to tape are separate operations.

Run the unit tests from the repository root.

```powershell
dotnet test UT/CaptureDeck.UT/CaptureDeck.UT.csproj -c Release
```

## Windows builds

The default build targets Windows x64 (Intel/AMD). Publish the app and probe for the architecture that will run them.

```powershell
$rid = 'win-arm64' # Or 'win-x64'
dotnet publish src/CaptureDeck.App -c Release -r $rid --self-contained
dotnet publish src/CaptureDeck.Probe -c Release -r $rid --self-contained
```

On Windows ARM64, CaptureDeck can open settings and check existing MOV files. DeckLink capture is currently available on Windows x64 with a compatible Blackmagic Desktop Video installation. Device support for future backends is validated separately.

## Desktop app

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

## Post-recording QC

After saving the MOV, CaptureDeck runs QC in the background. Put `ffmpeg.exe` and `ffprobe.exe` from a recent FFmpeg build on PATH or beside the executable. These tools read the saved file. Recording continues to use the DeckLink SDK directly.

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
