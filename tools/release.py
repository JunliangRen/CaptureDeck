"""Assemble previously published CI artifacts and verify the release before writing SHA256SUMS."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import re
import shutil
import struct
import zipfile

NAMES = ("CaptureRack-win-x64", "CaptureRack-win-arm64", "CaptureRack-QC-macos-arm64")
LOCK = Path(__file__).with_name("release-dependencies.json")


def require(ok, message):
    if not ok:
        raise ValueError(message)


def verify(path, version, commit):
    identity = f"{version}+{commit}".encode()
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "Duplicate ZIP members")
        require(not any(n.lower().endswith(".pdb") for n in names), "Unexpected PDB")
        require(archive.testzip() is None, "ZIP integrity failure")
        if path.stem == NAMES[2]:
            plists = [n for n in names if n.endswith(".app/Contents/Info.plist") and not n.startswith("__MACOSX/")]
            require(len(plists) == 1, "Expected one app bundle")
            contents = plists[0][:-len("Info.plist")]
            info = plistlib.loads(archive.read(plists[0]))
            require(info["CFBundleShortVersionString"] == version, "Incorrect macOS version")
            image = archive.read(contents + "MacOS/" + info["CFBundleExecutable"])
            require(struct.unpack_from("<II", image) == (0xfeedfacf, 0x100000c), "Incorrect Mach-O architecture")
            require(identity in archive.read(contents + "Resources/CaptureRack.Uno.dll"), "Incorrect macOS build commit")
        else:
            x64 = path.stem == NAMES[0]
            lock = json.loads(LOCK.read_text())
            for app in ("App", "Probe"):
                prefix = f"{app}/CaptureRack.{app}"
                exe = archive.read(prefix + ".exe")
                pe = struct.unpack_from("<I", exe, 60)[0]
                require(exe[:2] == b"MZ" and exe[pe:pe+4] == b"PE\0\0" and
                        struct.unpack_from("<H", exe, pe+4)[0] == (0x8664 if x64 else 0xaa64),
                        f"Incorrect executable architecture: {prefix}")
                require(identity in archive.read(prefix + ".dll"), f"Incorrect build identity: {prefix}")
                deps = json.loads(archive.read(prefix + ".deps.json"))
                require(deps["runtimeTarget"]["name"].endswith("/" + ("win-x64" if x64 else "win-arm64")),
                        f"Incorrect runtime target: {prefix}")
                require(lock["wasapi"]["package"] in deps["libraries"], "Incorrect WASAPI dependency version")
                require(hashlib.sha256(archive.read(app + "/NAudio.Wasapi.dll")).hexdigest() ==
                        lock["wasapi"]["sha256"][app], "Incorrect WASAPI assembly")
                for assembly in ("CaptureRack.Audio", "NAudio.Wasapi", "CaptureRack.DeckLink"):
                    require(f"{app}/{assembly}.dll" in names, f"Missing {assembly}")
            deps = json.loads(archive.read("App/CaptureRack.App.deps.json"))["libraries"]
            require((lock["package"] in deps) == x64, "Incorrect realtime preview dependency")
            for name, digest in lock["files"].items():
                if x64:
                    require(hashlib.sha256(archive.read(name)).hexdigest() == digest, f"Incorrect package content: {name}")
                else:
                    require(name not in names, f"Unexpected x64 file: {name}")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--artifacts", type=Path, help="assemble into a new directory from downloaded Actions artifacts")
    args = parser.parse_args()
    require(re.fullmatch("[0-9a-f]{40}", args.commit), "Expected full Git SHA")
    if args.artifacts:
        args.directory.mkdir(parents=True, exist_ok=False)
        for name in NAMES:
            source = args.artifacts / name
            if name == NAMES[2]:
                shutil.copyfile(source / (name + ".zip"), args.directory / (name + ".zip"))
            else:
                shutil.make_archive(str(args.directory / name), "zip", source)
    manifest = args.directory / "SHA256SUMS.txt"
    manifest.unlink(missing_ok=True)
    lines = []
    for name in NAMES:
        path = args.directory / (name + ".zip")
        lines.append(f"{verify(path, args.version, args.commit)}  {path.name}")
    manifest.write_text("\n".join(lines) + "\n", encoding="ascii", newline="\r\n")


if __name__ == "__main__":
    main()
