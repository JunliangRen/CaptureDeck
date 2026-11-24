# CaptureDeck agent memory

This is the repository handoff for coding agents. Start with the current Git status, the owning source, and [README.md](README.md). This file records architecture decisions and the last verified state. Update the relevant section when a change ships or its verification status changes.

## Working conventions

- Target .NET 11. The production Windows app uses native WinUI 3 and XAML. Uno Desktop is a separate QC prototype with its own build entry point.
- Save text files with CRLF in the working tree. Preserve existing package names, namespaces, CLI commands, and settings compatibility through directory moves. Tests live under `test/`.
- For commits, follow the user's Git identity using the repository-local author name and locally configured email. Keep GitHub usernames, machine-specific paths, private sample data, and credentials out of tracked files. Push or publish when the user requests it.

## Project map

| Project | Source directory | Responsibility |
| --- | --- | --- |
| CaptureDeck.App | `src/UI/CaptureDeck.App` | Native Windows UI, capture settings, DeckLink monitoring and recording, MOV QC |
| CaptureDeck.Uno | `src/UI/CaptureDeck.Uno` | Uno Skia Desktop for offline MOV and TS QC, report viewing and export |
| CaptureDeck.Probe | `src/CaptureDeck.Probe` | Windows command-line entry for DeckLink, FireWire, signal diagnostics, and QC |
| CaptureDeck.Core | `src/Capture/CaptureDeck.Core` | Portable capture contracts and backend registry |
| CaptureDeck.DeckLink | `src/Capture/CaptureCards/CaptureDeck.DeckLink` | Blackmagic COM interop, capture, monitoring, and current MOV writer |
| CaptureDeck.DirectShow | `src/Capture/CaptureDeck.DirectShow` | Windows DirectShow interop and FireWire / MSTape source |
| CaptureDeck.SignalCapture | `src/Capture/CaptureDeck.SignalCapture` | Sampled signal sources and diagnostics for Domesday Duplicator and RTL-SDR |
| CaptureDeck.Audio | `src/Capture/CaptureDeck.Audio` | Project reserved for standalone audio providers such as WASAPI and ASIO |
| CaptureDeck.Recording | `src/Processing/CaptureDeck.Recording` | Windows TS writer and file finalization |
| CaptureDeck.QualityControl | `src/Processing/CaptureDeck.QualityControl` | Portable MOV QC orchestration, TS parsing, and offline analysis |
| CaptureDeck.DeviceControl | `src/CaptureDeck.DeviceControl` | Independent tape transport contracts |

`CaptureDeck.slnx` is the main Windows solution. Build Uno directly from its project file. Put future vendor-SDK capture providers under `Capture/CaptureCards/`. Keep device-specific code near its provider and organize shared processing by the media it receives.

## Platform boundaries and sample flow

- Windows x64 is the currently supported live path for DeckLink and MSTape. The native App and Probe also build for Windows ARM64 and can run offline QC there. Check every hardware/backend combination separately before claiming ARM64 support.
- Uno runs the offline workflow on Windows and is packaged for macOS ARM64 through its own workflow. A macOS live source needs a native capture backend and device validation. MSTape, DirectShow COM, and WinUSB are Windows implementations.
- `ICaptureBackend` and `CaptureBackendRegistry` provide backend identity and lookup. The generic backend contract has typed requests, progress, and results. The native app currently resolves DeckLink directly and the FireWire CLI calls its MSTape implementation directly.
- `CaptureSampleHandler` delivers a `CaptureSample` synchronously. Its span borrows bytes from the source for the duration of that callback. Copy into owned storage before queueing or returning. The current contract carries optional stream-relative start and end timestamps and a discontinuity flag. The stream format is negotiated separately. False rejects delivery.
- `DirectShowSink` uses this contract. `TsRecording` copies accepted samples into pooled storage under a 64 MiB queue budget. Keep recording byte-preserving and report overrun explicitly. For future monitoring, bound its queue independently and signal sample gaps to a stateful decoder.
- `ITransportController` in `TransportControl.cs` models supported commands and observed state separately from host capture. An accepted command and an observed state change are distinct outcomes. Device control providers for AV/C, JLIP, and other RS-232 protocols are planned.

## Native WinUI and DeckLink

- Capture uses the Blackmagic SDK COM interfaces directly from managed code. A compatible Desktop Video driver/runtime is required for live capture. FFmpeg and FFprobe are used by offline MOV QC. The repo does not require a C++ build or an installed DeckLink SDK to compile.
- WinUI lets the operator choose the device, video input, audio input, input mode, and media formats. Routing is applied for the session and restored when it stops. The DeckLink CLI probe uses the routing already configured in Desktop Video Setup.
- Preview is the leftmost tab. The centered preview surface follows the selected/detected display aspect ratio and defaults to 4:3 before a mode is selected. Stereo peak meters sit vertically beside Preview. Scopes, Spectrum, and Events are separate tabs. The status bar shows configuration, counters, free space, and estimated record time.
- Settings are loaded from `%LOCALAPPDATA%\CaptureDeck\settings.json`. Preserve legacy flat settings migration and separate capture/control selection. Restore the last selections on launch and wait for the user to start preview. Settings use Windows display language by default, with English fallback and explicit overrides for English, Simplified Chinese, Traditional Chinese, and Japanese.
- MOV recording starts from an active preview with a fixed mode and an existing output directory. Names use local time and format tokens, for example `CaptureDeck_20261124_060236_633_10bit-Uncompressed_16bit-PCM.mov`. Stopping recording finalizes the file and starts QC while preview continues.
- The SDK supplies 10-bit `v210` or 8-bit UYVY video. The MOV writer uses `v210` or `2vuy` and preserves field order. Stereo signed 48 kHz PCM can be 16 or 32 bit. When preview uses 32-bit input for a 16-bit recording, use the high signed 16 bits. Readable no-input frames are saved and counted separately from valid or missing frames.
- Monitoring has a waveform, vectorscope, spectrum, clip and phase indicators, and recent events. Luma guides follow available input bit depth. Spectrum frequency follows sample rate and its level floor follows the input PCM depth. Do not equate sample bit depth with maximum frequency.

## FireWire, DirectShow, and TS

- FireWire / i.LINK is a CaptureDeck device source. In Windows it uses MSTape through DirectShow. MSTape-specific discovery and format selection live in `CaptureDeck.DirectShow/Windows/FireWire/MsTape/`. Shared COM bindings, media types, and callbacks are in `Windows/Interop/`.
- MSTape discovery enumerates the DirectShow video-input category, checks the `DeviceClassGUID`, and uses the device's unique ID when available. The negotiated media type decides downstream handling. TS output alone does not identify a FireWire device.
- `firewire list`, `probe`, `capture`, and `analyze` are available. Live capture currently accepts the native prefixed 192-byte MPEG-TS layout exposed to this probe. Preserve every received byte including the four-byte prefix. Offline analysis supports both 188-byte and 192-byte layouts. Do not generalize the live restriction to every MSTape driver or every other TS source.
- The shared TS writer leaves `.partial` on recording failure and protects existing outputs. The FireWire CLI writes a `.report.json` sidecar with byte counts, packet and timing diagnostics. TS transport analysis verifies packet structure and continuity, while decoded audio and video require separate checks.
- General DirectShow video/audio enumeration, capability reporting, and sample retrieval are next. Inspect advertised and negotiated media types separately. Optional capabilities include `IAMCrossbar` input routing. Uncompressed frames, compressed elementary streams, TS, DV, and waveform samples require different processing. Keep future MiniDV/MSDV and HDV behavior tied to verified device formats.

## RF and SDR diagnostics

- Keep the module name `SignalCapture` so it can cover RF and baseband signals such as CVBS. `Devices/Domesday/` contains DdD-specific diagnostics, `Devices/Sdr/RtlSdr/` contains RTL-SDR, and `Windows/` contains USB discovery and transport.
- DdD probe targets firmware through 3.2 and defaults to WinUSB. It recognizes legacy `1D50:603B` and protocol-v1 runtime `1209:2347`, inspects descriptors and stream endpoint, and checks protocol-v1 identity. USB protocol version alone does not establish the board revision. Streaming, MISRC capture, and live preview are future work.
- RTL-SDR diagnostics can list, probe, and configure a receiver, including frequency, sample rate, tuner gain, digital AGC, optional PPM, and supported VGA index. They use a matching native librtlsdr with WinUSB. RTL-SDR Blog V4 VGA needs the patched exports, plus a matching tuner and supported settings. The probe closes the diagnostic session without collecting I/Q.

## QC and shared decoder plans

- MOV QC decodes to the end and checks video frame and audio sample counts, field order, and PCM depth. It produces `.capture.json`, `.framemd5`, and `.mov.qctools.xml.gz`. Offline MOV checks are exposed in WinUI, Uno, and Probe. Use matching-architecture FFmpeg and FFprobe, with `cropdetect` for QCTools. Preserve recordings and prior reports across QC failures or retries.
- Uno runs TS analysis and exports JSON without FFmpeg or hardware. MOV QC in Uno uses the same QC engine and FFmpeg tools as the other entry points.
- MediaConch integration is deferred until an FFV1/MKV workflow exists. DV preview, general DirectShow capture, macOS live capture, and audio provider implementations are still planned.
- For future CVBS/RF preview, keep the reusable streaming decoder in the separate `vhs-decode-dotnet` repository. Start by replaying known CVBS samples at source rate. Share stateful sync and frame generation between its file preview and CaptureDeck, then add VHS RF and live hardware. Verify release licensing before distributing a linked shared library. This integration is currently a design only.

## Build and verification

Run from the repository root with the SDK selected by `global.json`.

```powershell
dotnet test test/CaptureDeck.UT/CaptureDeck.UT.csproj -c Release
dotnet test test/CaptureDeck.QualityControl.UT/CaptureDeck.QualityControl.UT.csproj -c Release
dotnet publish src/UI/CaptureDeck.App -c Release -r win-x64 --self-contained
dotnet publish src/CaptureDeck.Probe -c Release -r win-x64 --self-contained
dotnet build src/UI/CaptureDeck.Uno/CaptureDeck.Uno.csproj -c Release
```

- `.github/workflows/windows.yml` tests and publishes Windows x64 and ARM64 packages. `.github/workflows/uno-macos.yml` builds the Uno macOS ARM64 `.app` and verifies the executable architecture. Check actual workflow runs for the current commit before reporting CI success.
- Verification at `252e5ed` included 35 Windows tests and 6 portable QC tests. The synthetic DirectShow graph passed complete, start-only, and absent timestamps. File replay kept 188- and 192-byte packet streams byte-for-byte. Windows x64 and ARM64 probe builds passed. The x64 FireWire device list ran and returned no devices.
- The earlier directory-migration change also passed Windows x64 and ARM64 WinUI and Probe publishing and an Uno build on Windows. HM-DHX2 real-device streaming remains unverified in CaptureDeck. Validate DeckLink with a known A/V signal when available. Check actual drivers, CI results, assets, and connected hardware when resuming.
