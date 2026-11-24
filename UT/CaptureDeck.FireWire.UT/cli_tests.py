"""Offline executable-level tests; never enumerate, open, or control hardware."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    exe = str(Path(sys.argv[1]).resolve())
    version = subprocess.run([exe, "--version"], capture_output=True, encoding="utf-8", check=True).stdout.strip()
    base = Path(sys.argv[2]).resolve()
    base.mkdir(parents=True, exist_ok=True)
    checks = 0

    def run(*args, expected=0):
        nonlocal checks
        completed = subprocess.run([exe, *map(str, args)], capture_output=True,
                                   encoding="utf-8", timeout=15)
        assert completed.returncode == expected, (args, completed.returncode, completed.stdout, completed.stderr)
        objects = [json.loads(line) for line in completed.stdout.splitlines()]
        assert objects and all(item["schema_version"] == 1 and item["version"] == version for item in objects)
        checks += 1
        return objects[-1]

    with tempfile.TemporaryDirectory(prefix="offline-", dir=base) as name:
        folder = Path(name)
        assert folder.resolve().is_relative_to(base)
        # More than one read buffer exercises streaming detection/analysis with
        # a 192-byte packet split at the 1 MiB executable read boundary.
        packets = []
        for index in range(6000):
            packet = bytearray([0xff] * 188)
            packet[:4] = bytes([0x47, 0x01, 0x00, 0x10 | (index % 16)])
            packets.append(bytes(packet))
        standard = folder / "中文 样本.ts"
        native = folder / "中文 原始.ts192"
        standard.write_bytes(b"".join(packets))
        native.write_bytes(b"".join((n * 1000).to_bytes(4, "big") + p for n, p in enumerate(packets)))
        for path, size, mode in [(standard, 188, "ts188"), (native, 192, "ts192")]:
            result = run("analyze", "--input", path, "--json")
            assert result["success"] and result["format"] == mode
            assert result["analysis"]["input_bytes"] == 6000 * size
            assert result["analysis"]["packets"] == 6000
            assert result["analysis"]["continuity_errors"] == 0
            assert result["analysis"]["pids"][0]["pid"] == 256
            assert result["input_path"] == str(path)

        short = folder / "one.ts192"
        short.write_bytes(native.read_bytes()[:192])
        run("analyze", "--input", short, "--json", expected=4)
        assert run("analyze", "--input", short, "--format", "ts192", "--json")["analysis"]["packets"] == 1
        truncated = folder / "truncated.ts192"
        truncated.write_bytes(native.read_bytes()[:-1])
        result = run("analyze", "--input", truncated, "--json", expected=4)
        assert result["analysis"]["trailing_bytes"] == 191
        corrupt = folder / "bad-sync.ts"
        data = bytearray(standard.read_bytes())
        data[7 * 188] = 0
        corrupt.write_bytes(data)
        assert run("analyze", "--input", corrupt, "--format", "ts188", "--json", expected=4)["analysis"]["sync_errors"] == 1
        empty = folder / "empty.ts"
        empty.write_bytes(b"")
        run("analyze", "--input", empty, "--json", expected=4)
        run("analyze", "--input", folder / "missing.ts", "--json", expected=5)
        run("unknown", "--json", expected=2)
        run("probe", "--json", expected=2)
        run("analyze", "--input", standard, "--format", "wrong", "--json", expected=2)
        run("analyze", "--input", standard, "--input", native, "--json", expected=2)
        run("analyze", "--input", "--json", expected=2)
        run("list", "--output", standard, "--json", expected=2)
        for number in ["nan", "inf", "-1", "1foo", "31536001"]:
            run("capture", "--device", "invalid", "--output", folder / "never-created.ts192",
                "--duration", number, "--json", expected=2)
        run("capture", "--device", "invalid", "--output", folder / "never-created.ts192",
            "--no-data-timeout", "0", "--json", expected=2)
        # Existing-path checks happen before COM/device activation.
        result = run("capture", "--device", "invalid", "--output", standard, "--json", expected=5)
        assert not result["data_file_created"] and not result["stream_started"]
        assert standard.read_bytes() == b"".join(packets)
        assert not Path(str(standard) + ".report.json").exists()
        assert not Path(str(standard) + ".partial").exists()
        assert not (folder / "never-created.ts192.partial").exists()
    print(f"cli: {checks} offline executable checks passed")


if __name__ == "__main__":
    main()
