# Windows RTL-SDR runtime

App and Probe ship rtlsdr.dll and libusb-1.0.dll for the selected Windows RID. Both use the static MSVC runtime. The receiver uses the Windows WinUSB driver.

- RTL-SDR is Osmocom v2.0.2, commit [619ac318](https://github.com/osmocom/rtl-sdr/tree/619ac3186ea0ffc092615e1f59f7397e5e6f668c), with the [MISRC-GUI patch](https://github.com/harrypm/MISRC-GUI/blob/fb9f3ee9dee7abd5a1bfe3236e27df30047e288f/third_party/rtl-sdr/patches/0001-misrc.patch) for Blog V4 VGA control. See COPYING-rtl-sdr and the individual source notices.
- libusb is [v1.0.29](https://github.com/libusb/libusb/tree/v1.0.29). See COPYING-libusb and the individual source notices.

The accompanying source.zip contains the corresponding source trees (with the patch applied) and the CMake build recipe. Extract it into an empty directory. In that directory run the following with CMake and the Visual Studio 2026 C++ tools for both targets.

```powershell
foreach ($arch in 'x64', 'ARM64') {
    cmake -S . -B "build-$arch" -G 'Visual Studio 18 2026' -A $arch
    cmake --build "build-$arch" --config Release
}
```

Use the two DLLs in each build directory's Release subdirectory for its matching RID. On an update, replace the source archive, notices and release dependency hashes together.
