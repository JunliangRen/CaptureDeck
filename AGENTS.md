# CaptureRack agent memory

This is the repository handoff for coding agents. Start with the current Git status, the owning source, and [README.md](README.md). This file records architecture decisions and the last verified state. Update the relevant section when a change ships or its verification status changes.

## Working conventions

- Target .NET 11. The production Windows app uses native WinUI 3 and XAML. Uno Desktop is a separate QC prototype with its own build entry point.
- Save text files with CRLF in the working tree. Preserve existing package names, namespaces, CLI commands, and settings compatibility through directory moves. Tests live under `test/`.
- For commits, follow the user's Git identity using the repository-local author name and locally configured email. Keep GitHub usernames, machine-specific paths, private sample data, and credentials out of tracked files. Push or publish when the user requests it.

## Project map

| Project | Source directory | Responsibility |
| --- | --- | --- |
| CaptureRack.App | `src/UI/CaptureRack.App` | Native Windows UI, capture settings, DeckLink monitoring and recording, MOV QC |
| CaptureRack.Uno | `src/UI/CaptureRack.Uno` | Uno Skia Desktop for offline MOV and TS QC, report viewing and export |
| CaptureRack.Probe | `src/CaptureRack.Probe` | Windows command-line entry for DeckLink, FireWire, signal diagnostics, and QC |
| CaptureRack.Core | `src/Capture/CaptureRack.Core` | Portable capture contracts and backend registry |
| CaptureRack.DeckLink | `src/Capture/CaptureCards/CaptureRack.DeckLink` | Blackmagic COM interop, capture, monitoring, and current MOV writer |
| CaptureRack.DirectShow | `src/Capture/CaptureRack.DirectShow` | Windows generic DirectShow diagnostics and FireWire / MSTape source |
| CaptureRack.SignalCapture | `src/Capture/CaptureRack.SignalCapture` | Sampled signal sources and diagnostics for Domesday Duplicator and RTL-SDR |
| CaptureRack.Audio | `src/Capture/CaptureRack.Audio` | Shared-mode WASAPI speaker monitoring, PCM meters and gain, channel-pair routing and surround downmix |
| CaptureRack.Recording | `src/Processing/CaptureRack.Recording` | Windows TS writer and file finalization |
| CaptureRack.QualityControl | `src/Processing/CaptureRack.QualityControl` | Portable MOV QC orchestration, TS parsing, and offline analysis |
| CaptureRack.DeviceControl | `src/CaptureRack.DeviceControl` | Independent tape transport contracts |

`CaptureRack.slnx` is the main Windows solution. Build Uno directly from its project file. Put future vendor-SDK capture providers under `Capture/CaptureCards/`. Keep device-specific code near its provider and organize shared processing by the media it receives.

## Platform boundaries and sample flow

- Windows x64 is the currently supported live path for DeckLink and MSTape. The native App and Probe also build for Windows ARM64 and can run offline QC there. Check every hardware/backend combination separately before claiming ARM64 support.
- Uno runs the offline workflow on Windows and is packaged for macOS ARM64 through its own workflow. A macOS live source needs a native capture backend and device validation. MSTape, DirectShow COM, and WinUSB are Windows implementations.
- `ICaptureBackend` and `CaptureBackendRegistry` provide backend identity and lookup. The generic backend contract has typed requests, progress, and results. The native app currently resolves DeckLink directly and the FireWire CLI calls its MSTape implementation directly.
- `CaptureSampleHandler` delivers a `CaptureSample` synchronously. Its span borrows bytes from the source for the duration of that callback. Copy into owned storage before queueing or returning. The current contract carries optional stream-relative start and end timestamps and a discontinuity flag. The stream format is negotiated separately. False rejects delivery.
- `DirectShowSink` uses this contract. `TsRecording` copies accepted samples into pooled storage under a 64 MiB queue budget. Keep recording byte-preserving and report overrun explicitly. For future monitoring, bound its queue independently and signal sample gaps to a stateful decoder.
- `ITransportController` in `TransportControl.cs` models supported commands and observed state separately from host capture. An accepted command and an observed state change are distinct outcomes. Device control providers for AV/C, JLIP, and other RS-232 protocols are planned.

## Development and verification

The Windows solution maps its Any CPU configuration to x64 for App and Probe. The libraries and tests use Any CPU. MSBuild verified the Debug and Release mappings and built the Release solution. The shared CaptureRack.App launch profile starts the WinUI project.

The native WinUI app enables unsafe compilation for generated WinRT and LibraryImport code. The disk-space query retains the UTF-16 Win32 entry point and is covered by a Unicode-directory test.

When editing code, review compiler diagnostics for obsolete APIs and run both Roslyn and ReSharper inspections. For the Windows solution, run `dotnet format CaptureRack.slnx --verify-no-changes --severity info` and `jb inspectcode CaptureRack.slnx --swea --severity=SUGGESTION --output=<temporary-sarif-path>`. Use an existing JetBrains ReSharper Command Line Tools installation, or install `JetBrains.ReSharper.GlobalTools` with `dotnet tool install --tool-path` in a temporary directory and invoke `jb` from there. Keep reports outside tracked files. If either tool is unavailable or cannot load the solution, run the available checks and report the missing coverage.

Review findings from both tools together. Merge duplicates and assess each relevant recommendation against actual callers, supported platforms, public contracts, implicit uses such as reflection or XAML, and interop or resource lifetime requirements. Apply and retain targeted changes when source review and verification show that they preserve behavior and compatibility. Recheck affected diagnostics and run relevant builds or tests. Leave risky or uncertain recommendations for separate work and explain why in the handoff.

DeckLink COM and the Win32 window callbacks use runtime marshalling. Keep their declarations compatible with the current activation, casting, callback and release paths.

Keep WinUI preview, speaker playback and recording queues independent. Preserve capture buffers before returning from borrowed sample callbacks. Treat preview skips, recording loss and unavailable telemetry as distinct observations. Report software recording failures and preserve interrupted media for QC.

For supported raw DirectShow frame layouts with a legacy VIDEOINFOHEADER, use the progressive rendering default after checking matching extended media types and the Blackmagic AVStream exception. Keep conflicting flags and compressed streams unresolved when no stronger evidence exists. The original interlace flags remain available separately from fallback flags.

Windows App and Probe builds require the RID-specific RTL-SDR and libusb DLLs from `third_party/rtl-sdr`. Both build and publish include the corresponding source and notices. The x64 bundle enumerated and probed a physical RTL-SDR Blog V4 (R828D, manual VGA available) without an environment override. ARM64 device operation is unverified.

Domesday protocol-v1 identity reads retry short responses up to three total attempts. A complete mismatched identity fails immediately. Keep this handshake independent of FIFO telemetry polling. Ten consecutive WinUI refreshes on the local 3.2.0 device returned its version with no diagnostic banner.

Use MISRC-GUI upstream main when comparing RF capture behavior. Bind RF decode options to the public win-x64 Realtime package capabilities. Package 2.35.0-preview.ci.27 passed the Windows RF replay regression, and its managed and native DLLs matched the published x64 app output. Use its post-block lock snapshot for receiver status and its signed signal planes for RF line measurements. Legacy Domesday Duplicator and protocol-v1 devices require separate endpoint and firmware checks. Gateware identity and a successful full-file verification are both required for a hardware ramp baseline.

RF preview safe guides and the center cross use the displayed field pair's active-video rectangle. The NTSC 480 crop option requires an explicitly identified NTSC picture mode. Its label and bands use the same source-row counts. NTSC full-raster replay at 640 x 525 verified 40 top and 5 bottom lines.

The RF oscilloscope offers an input FFT in both file and receiver sessions, including RF-only mode. Level windows retain up to 4096 consecutive ADC or I/Q samples per measurement. WinUI file replay at 40 MSPS verified a 5 MHz half-scale tone and switching the VHS toolbar between NTSC, PAL, SECAM and MESECAM. The latter two offer the packaged VHS decoder only.

WinUI checks verified stable RF toolbar positions when opening, selecting and closing all five Betamax profiles, five NTSC LaserDisc tracks and three NTSC VHS decode modes. RF audio meter visibility changes the monitor area below the toolbar. Keep saved VHS profile values compatible (0 video, 1 independent Hi-Fi audio, 2 S-VHS).

Windows x64 WinUI replay has been checked with 40 MSPS PAL S-VHS, NTSC S-VHS-ET, SuperBeta, SuperBeta Hi-Fi and ED Beta captures. Picture and Y/U/V traces advanced in each mode. Both Hi-Fi modes cleared and reacquired the stereo meter across an inserted RF interruption. SuperBeta video-only used the Hi-Fi capture with audio disabled. Conventional NTSC S-VHS still lacks a real fixture. For the host regression, set `CAPTURERACK_TAPE_FIXTURES` to a directory containing `svhs-pal.s16`, `svhs-ntsc-et.s16`, `superbeta.s16` and `edbeta.s16`, and run `RfPackagedPreviewTests` with `TestRealtimeRf=true`.

Both test projects use xUnit.net v3 through the `xunit.v3.mtp-off` package to keep VSTest working on .NET 11. The Windows and portable QC suites passed locally with the Visual Studio adapter 4.0.1. Both suites also passed with xUnit1051 treated as an error. Pass the xUnit test cancellation token to cancellable calls and link it to existing operation timeouts. Keep bounded waits for resource cleanup independent of test cancellation. The RF CI filter discovered the packaged preview tests. Fixture-backed cases were skipped without local fixture paths.

Run both Windows and portable QC test projects for changes affecting capture. Enable FFmpeg integration with CAPTURERACK_TEST_SIGNAL_QC=1. Keep operator-triggered hardware runs out of CI. Keep the App, Probe and Uno display versions in sync and exclude PDBs from all release packages. Run the candidate workflow at the intended commit before publishing the three verified archives.

See the [validation entry points and support matrix](README.md#validation-and-hardware-support) and the [versioned engineering and hardware evidence](docs/validation/through-v0.7.0.md).
