"""Run from a checked out candidate on Windows. Every invocation owns a new output directory."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TESTS = {
    "ConsecutiveClipsUseOnePreviewAndIndependentCountersWhileVerificationIsPending": "qc",
    "EncodesLiveAndRecoversCompleteClusters": "write",
    "EncoderInputStallFillsTheDeckLinkQueueWithoutACaptureGap": "write",
    "LosingActiveSpeakerDuringRecordingPreservesAllMedia": "input",
    "RetiringWorkerCannotClearNewSessionOrPublishItsClock": "input",
    "DroppedSampleReportCorrelatesSequenceUsbFifoAndWriterQueue": "transport",
}


def run(*args, **kwargs):
    return subprocess.run(args, cwd=ROOT, check=True, **kwargs)


def short(out, report):
    env = dict(os.environ, CAPTURERACK_TEST_SIGNAL_QC="1", CAPTURERACK_VALIDATION_OUTPUT=str(out))
    project = "test/CaptureRack.UT/CaptureRack.UT.csproj"
    result = subprocess.run(["dotnet", "test", project, "-c", "Release", "--filter",
                             "|".join("FullyQualifiedName~" + name for name in TESTS),
                             "--results-directory", str(out), "--logger", "trx;LogFileName=short.trx"],
                            cwd=ROOT, env=env)
    cases = []
    for node in ET.parse(out / "short.trx").findall(".//{*}UnitTestResult"):
        name = node.get("testName")
        method = name.split("(")[0].split(".")[-1]
        if method in TESTS:
            cases.append(dict(test=method, name=name, stage=TESTS[method], outcome=node.get("outcome"),
                              duration=node.get("duration"), error=node.findtext(".//{*}Message")))
    report["cases"] = cases
    report["measurements"] = {p.stem: json.loads(p.read_text()) for p in sorted(out.glob("measurement-*.json"))}
    for case in cases:
        if case["outcome"] != "Passed":
            report["stage"] = case["stage"]
            raise RuntimeError(case["error"] or case["name"])
    if result.returncode or {c["test"] for c in cases} != set(TESTS):
        raise RuntimeError("The selected regression suite did not complete.")


def recovery(out, probe, report):
    target = out / "capture.mkv"
    partial = Path(str(target) + ".partial.mkv")
    journal = Path(str(partial) + ".checkpoint.json")
    child = subprocess.Popen([probe, "validate", "crash-source", target], cwd=ROOT)
    try:
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if child.poll() is not None:
                raise RuntimeError(f"Capture source exited with {child.returncode}")
            if journal.exists() and json.loads(journal.read_bytes())["completeClusterBytes"] > 0:
                break
            time.sleep(0.05)
        else:
            raise TimeoutError("No complete cluster checkpoint")
    finally:
        if child.poll() is None:
            child.kill()
        child.wait()
    checkpoint = json.loads(journal.read_bytes())
    report["checkpoint"] = checkpoint
    report["stage"] = "recovery"
    recovered = out / "recovered.mkv"
    msg = run(probe, "recover-mkv", partial, recovered, capture_output=True, text=True).stdout
    prefix = int(re.search(r"Recovered (\d+) bytes", msg)[1])
    report["recoveredPrefixBytes"] = prefix
    if not 0 < checkpoint["completeClusterBytes"] <= prefix <= partial.stat().st_size:
        raise ValueError("Recovered prefix does not cover the durable checkpoint")
    durable = out / "durable-prefix.mkv"
    remaining = checkpoint["durableBytes"]
    if not 0 < remaining <= partial.stat().st_size:
        raise ValueError("Invalid durable byte count")
    with partial.open("rb") as source, durable.open("xb") as dest:
        while remaining:
            block = source.read(min(remaining, 1024 * 1024))
            dest.write(block)
            remaining -= len(block)
    exact = run(probe, "recover-mkv", durable, out / "checkpoint-recovered.mkv", capture_output=True, text=True)
    if int(re.search(r"Recovered (\d+) bytes", exact.stdout)[1]) != checkpoint["completeClusterBytes"]:
        raise ValueError("Checkpoint does not identify the exact recoverable boundary")
    report["stage"] = "qc"
    run("ffmpeg", "-v", "error", "-xerror", "-i", recovered, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-")
    streams = json.loads(run("ffprobe", "-v", "error", "-count_frames", "-show_entries",
                             "stream=codec_type,nb_read_frames", "-of", "json", recovered,
                             capture_output=True).stdout)["streams"]
    report["decodedFramesByStream"] = {s["codec_type"]: int(s["nb_read_frames"]) for s in streams}
    if any(report["decodedFramesByStream"].get(t, 0) <= 0 for t in ("audio", "video")):
        raise ValueError("Recovered tracks must both decode")


def hardware(args, out, probe, report):
    if not args.device or not 1 <= args.seconds <= 28800:
        raise ValueError("Supply --device and a duration between 1 s and 8 hours")
    report.update(device=args.device, seconds=args.seconds, format="40 MSPS ramp FLAC",
                  estimatedBytes=40_000_000 * args.seconds, freeBytes=shutil.disk_usage(out).free)
    if report["freeBytes"] < report["estimatedBytes"]:
        raise ValueError("Free space below estimated FLAC size (2:1)")
    report["identity"] = json.loads(run(probe, "rf", "probe", args.device, "--json", capture_output=True).stdout)
    report["stage"] = "transport"
    try:
        result = subprocess.run([probe, "validate", "ddd-ramp", args.device, out / "capture", str(args.seconds)],
                                cwd=ROOT, capture_output=True, text=True)
    finally:
        for pattern, key in (("*.fifo.json", "capture"), ("*.ramp.json", "verification")):
            for p in out.glob(pattern):
                report[key] = json.loads(p.read_bytes())
    if result.stdout.strip():
        report["driver"] = json.loads(result.stdout)
    fifo = report.get("capture", {})
    if result.returncode:
        cause = fifo.get("Error") or result.stderr.strip() or "Hardware capture failed"
        if fifo and not fifo.get("Error"):
            report["stage"] = "qc"
        elif "queue" in cause.lower() or "writ" in cause.lower():
            report["stage"] = "write"
        raise RuntimeError(cause)
    report["stage"] = "qc"
    count = report["verification"]["VerifiedFileSamples"]
    if not (count > 0 and count == fifo["InputSamples"] == fifo["WrittenSamples"] and
            fifo["ValidIntervals"] > 0 and not any(fifo[k] for k in
            ("OverflowEvents", "DroppedSamples", "MissedIntervals", "CounterSaturated", "PollingDisabled"))):
        raise ValueError("Ramp count, FIFO coverage or loss check failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", choices=["short", "recovery", "ddd", "av-clock", "file"])
    parser.add_argument("output", type=Path, help="new directory for this run")
    parser.add_argument("--probe", type=Path)
    parser.add_argument("--device")
    parser.add_argument("--seconds", type=int, default=600)
    parser.add_argument("--media", type=Path)
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    report = dict(schema=1, profile=args.profile, evidence={"ddd": "hardware", "av-clock": "hardware", "file": "file"}.get(args.profile, "simulation"),
                  stage="input", status="failed", firstError=None)
    try:
        report["commit"] = run("git", "rev-parse", "HEAD", capture_output=True, text=True).stdout.strip()
        report["version"] = ET.parse(ROOT / "src/CaptureRack.Probe/CaptureRack.Probe.csproj").findtext(".//Version")
        for tool in ("ffmpeg", "ffprobe"):
            report[tool] = run(tool, "-version", capture_output=True, text=True, errors="replace").stdout.splitlines()[0]
        probe = args.probe.resolve() if args.probe else None
        if probe and probe.is_file():
            report["probeVersion"] = run(probe, "validate", "identity", capture_output=True, text=True).stdout.strip()
            if report["probeVersion"] != report["version"] + "+" + report["commit"]:
                raise ValueError("Probe version or commit differs from this checkout")
        if args.profile == "short":
            short(out, report)
        elif not probe or not probe.is_file():
            raise ValueError("Provide --probe pointing to the candidate CaptureRack.Probe executable")
        elif args.profile == "recovery":
            report["stage"] = "write"
            recovery(out, probe, report)
        elif args.profile == "av-clock":
            if not args.device or not args.device.isdigit() or not 1 <= args.seconds <= 28800:
                raise ValueError("Supply --device INDEX and a duration between 1 s and 8 hours")
            report.update(device=args.device, seconds=args.seconds, format="v210 / 48 kHz stereo PCM")
            result = subprocess.run([probe, "validate", "av-clock", args.device, str(args.seconds)],
                                    cwd=ROOT, capture_output=True, text=True)
            if result.stdout.strip():
                report["capture"] = json.loads(result.stdout)
            if result.returncode:
                raise RuntimeError(result.stderr.strip() or "A/V clock measurement failed")
        elif args.profile == "ddd":
            hardware(args, out, probe, report)
        else:
            report["stage"] = "qc"
            if not args.media:
                raise ValueError("Provide --media")
            # QC writes sidecars next to its input. Keep the original and its capture log intact.
            media = out / args.media.name
            shutil.copy2(args.media, media)
            for suffix in (".recording.json", ".encoding.json"):
                sidecar = Path(str(args.media) + suffix)
                if sidecar.exists():
                    shutil.copy2(sidecar, Path(str(media) + suffix))
            capture_log = args.media.with_suffix(".capture.json")
            if capture_log.exists():
                shutil.copy2(capture_log, media.with_suffix(".capture.json"))
            result = run(probe, "qc", media, capture_output=True, text=True)
            log = next(Path(line) for line in result.stdout.splitlines() if line.endswith(".capture.json"))
            report["verification"] = json.loads(log.read_bytes())
        report.update(status="passed", stage="complete")
    except Exception as error:
        report["firstError"] = str(error)
        raise
    finally:
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        (out / "validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
