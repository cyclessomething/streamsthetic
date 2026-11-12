"""Read all four installed virtual cameras concurrently at the requested frame rate.

Requires the current camera DLL already installed, ffmpeg on PATH, and the local PCM1
recording. Never installs cameras, connects to a phone, or stops another process.
Close Streamsthetic/other camera consumers first. Logs and frame hashes are retained.
"""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receiver", type=Path, default=ROOT / "windows/receiver/x64/Release/StreamstheticReceiver.exe")
    parser.add_argument("--recording", type=Path, default=ROOT / "tools/testdata/phone-1080p60-10s.pcm1")
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg"))
    parser.add_argument("--seconds", type=int, default=5)
    parser.add_argument("--fps", type=int, choices=(30, 60), default=30)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    if not args.receiver.is_file() or not args.recording.is_file() or not args.ffmpeg:
        parser.error("Build the receiver, supply a PCM1 recording, and install ffmpeg first.")
    if not 2 <= args.seconds <= 7:
        parser.error("--seconds must be 2..7 for the ten-second recording")
    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=False)
    else:
        args.output_dir = Path(tempfile.mkdtemp(prefix="streamsthetic-cameras-e2e-"))
    print(f"Logs: {args.output_dir}", flush=True)
    receivers, readers, files, results = {}, {}, [], {}
    hidden = subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0
    started = time.monotonic()
    try:
        def launch(command, output, errors):
            stdout = open(args.output_dir / output, "w", encoding="utf-8")
            stderr = open(args.output_dir / errors, "w", encoding="utf-8")
            files.extend((stdout, stderr))
            return subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, creationflags=hidden)

        for slot in range(1, 5):
            receivers[slot] = launch(
                [str(args.receiver), "--slot", str(slot), "--replay", str(args.recording), "--realtime", "--no-register"],
                f"receiver-{slot}.log", f"receiver-{slot}.err")
        for slot in range(1, 5):
            camera = "Streamsthetic" + (f" {slot}" if slot > 1 else "") + " (Windows Virtual Camera)"
            # The Windows bridge rejects its rounded upper boundary (literal 60 and even
            # 10000000/166667). Requesting 59.94 selects the source's advertised 60 fps mode;
            # ffmpeg reports 60 fps. Retain the 300-frame expectation and record the request.
            capture_rate = "59.94" if args.fps == 60 else str(args.fps)
            readers[slot] = launch(
                [args.ffmpeg, "-hide_banner", "-loglevel", "warning", "-f", "dshow", "-rtbufsize", "256M",
                 "-video_size", "1280x720", "-framerate", capture_rate, "-i", f"video={camera}",
                 "-t", str(args.seconds), "-an", "-fps_mode", "passthrough", "-f", "framehash", "-"],
                f"camera-{slot}.framehash", f"camera-{slot}.err")

        # All processes run together; one deadline bounds the whole test, not each individual child.
        for process in [*readers.values(), *receivers.values()]:
            process.wait(timeout=max(0.1, 40 - (time.monotonic() - started)))
        for file in files:
            file.flush()
        failures = []
        for slot in range(1, 5):
            receiver_log = (args.output_dir / f"receiver-{slot}.log").read_text(encoding="utf-8", errors="replace")
            receiver_error = (args.output_dir / f"receiver-{slot}.err").read_text(encoding="utf-8", errors="replace")
            camera_error = (args.output_dir / f"camera-{slot}.err").read_text(encoding="utf-8", errors="replace")
            hashes = (args.output_dir / f"camera-{slot}.framehash").read_text(encoding="utf-8", errors="replace")
            rows = [line.split(",") for line in hashes.splitlines() if line and not line.startswith("#")]
            dts = [int(row[1]) for row in rows]
            frame_count = len(rows)
            distinct_frames = len({row[-1].strip() for row in rows})
            written_match = re.search(r"written to camera (\d+)", receiver_log)
            written = int(written_match[1]) if written_match else 0
            expected = args.seconds * args.fps
            errors = []
            if receivers[slot].returncode != 0 or receiver_error.strip():
                errors.append(f"receiver exit {receivers[slot].returncode}: {receiver_error.strip()}")
            if readers[slot].returncode != 0:
                errors.append(f"ffmpeg exit {readers[slot].returncode}: {camera_error.strip()}")
            if not expected - 5 <= frame_count <= expected + 5:
                errors.append(f"expected {expected} (+/-5) captured frames, got {frame_count}")
            if re.search(r"non.?monoton|invalid.*(?:dts|timestamp)", camera_error, re.IGNORECASE):
                errors.append("ffmpeg reported invalid/non-monotonic timestamps")
            if any(right <= left for left, right in zip(dts, dts[1:])):
                errors.append("captured frame timestamps are not strictly increasing")
            if written <= 0:
                errors.append("receiver did not report writing any camera frames")
            if distinct_frames < 2:
                errors.append("camera returned only a static frame (possibly black)")
            results[slot] = dict(requested_rate=capture_rate, expected_frames=expected,
                                 frames=frame_count, distinct_frames=distinct_frames, written=written,
                                 reader_exit=readers[slot].returncode, receiver_exit=receivers[slot].returncode, errors=errors)
            if errors:
                failures.extend(f"slot {slot}: {error}" for error in errors)
            print(f"{'FAIL' if errors else 'PASS'}: slot {slot}: {frame_count} frames at {args.fps} fps, "
                  f"{distinct_frames} distinct pictures, {written} frames written", flush=True)
        (args.output_dir / "result.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        if failures:
            raise AssertionError("\n".join(failures))
    finally:
        for process in [*readers.values(), *receivers.values()]:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        for file in files:
            file.close()
    print("FOUR CAMERA END-TO-END SELF-TEST PASS", flush=True)


if __name__ == "__main__":
    main()
