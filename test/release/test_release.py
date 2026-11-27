import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from release import verify


class ReleaseTests(unittest.TestCase):
    def test_windows_package_rejects_wrong_identity_architecture_dependency_and_symbols(self):
        commit = "a" * 40
        exe = bytearray(96)
        exe[:2] = b"MZ"
        struct.pack_into("<I", exe, 60, 64)
        exe[64:68] = b"PE\0\0"
        struct.pack_into("<H", exe, 68, 0xaa64)
        deps = json.dumps({"runtimeTarget": {"name": "net11.0/win-arm64"}, "libraries": {"NAudio.Wasapi/2.3.0": {}}})
        files = {}
        for app in ("App", "Probe"):
            prefix = f"{app}/CaptureRack.{app}"
            files[prefix + ".exe"] = exe
            files[prefix + ".dll"] = ("0.7.0+" + commit).encode()
            files[prefix + ".deps.json"] = deps
            for name in ("CaptureRack.Audio", "NAudio.Wasapi", "CaptureRack.DeckLink"):
                files[f"{app}/{name}.dll"] = b"dependency"
        with tempfile.TemporaryDirectory() as temp:
            lock = Path(temp) / "lock.json"
            digest = hashlib.sha256(b"dependency").hexdigest()
            lock.write_text(json.dumps({"package": "realtime-test", "files": {}, "wasapi": {
                "package": "NAudio.Wasapi/2.3.0", "sha256": {"App": digest, "Probe": digest}}}))
            override = patch("release.LOCK", lock)
            override.start()
            self.addCleanup(override.stop)
            path = Path(temp) / "CaptureRack-win-arm64.zip"
            def pack(entries):
                with zipfile.ZipFile(path, "w") as archive:
                    for name, content in entries.items():
                        archive.writestr(name, content)
            pack(files)
            verify(path, "0.7.0", commit)
            for version, sha in (("0.7.1", commit), ("0.7.0", "b" * 40)):
                with self.assertRaises(ValueError):
                    verify(path, version, sha)
            for entries in (files | {"App/debug.PdB": b""},
                            files | {"Probe/CaptureRack.Probe.exe": bytes(exe[:68]) + b"\x64\x86"},
                            files | {"App/NAudio.Wasapi.dll": b"replaced"},
                            {k: v for k, v in files.items() if k != "App/NAudio.Wasapi.dll"}):
                pack(entries)
                with self.assertRaises((KeyError, ValueError)):
                    verify(path, "0.7.0", commit)


if __name__ == "__main__":
    unittest.main()
