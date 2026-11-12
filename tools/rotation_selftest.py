"""Exercise the real receiver's CPU/GPU rotation and mirror on labelled synthetic H.264.

No phone or virtual camera is opened. A free receiver slot is required (default 4).
Expected contract: clockwise rotation, then horizontal mirroring in the displayed picture.
Saves preview/output PNGs, raw frames, logs, and results for inspection.
"""
import argparse
import ctypes
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import time

from PIL import Image, ImageDraw, ImageFont
from receiver_slots_selftest import Preview

ROOT = Path(__file__).resolve().parents[1]
HEADER = struct.Struct(">IBBHIqq")
COLORS = {"R": (255, 0, 0), "G": (0, 255, 0), "B": (0, 0, 255), "Y": (255, 255, 0)}
# Independently specified corner order TL, TR, BL, BR after clockwise rotation.
CORNERS = {0: "RGBY", 90: "BRYG", 180: "YBGR", 270: "GYRB"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receiver", type=Path, default=ROOT / "windows/receiver/x64/Release/StreamstheticReceiver.exe")
    parser.add_argument("--slot", type=int, choices=range(1, 5), default=4)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--output-size", choices=("1280x720", "640x360", "720x1280"), default="1280x720")
    args = parser.parse_args()
    output_width, output_height = map(int, args.output_size.split("x"))
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg or not args.receiver.is_file():
        parser.error("Build the receiver and install ffmpeg first")
    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=False)
    else:
        args.output_dir = Path(tempfile.mkdtemp(prefix="streamsthetic-rotation-"))
    out = args.output_dir
    print(f"Evidence: {out}", flush=True)
    source = Image.new("RGB", (640, 360))
    draw = ImageDraw.Draw(source)
    font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 28)
    for name, box in zip("RGBY", [(0, 0, 319, 179), (320, 0, 639, 179), (0, 180, 319, 359), (320, 180, 639, 359)]):
        draw.rectangle(box, fill=COLORS[name])
        draw.text((box[0] + 15, box[1] + 10), name + " TOP" if box[1] == 0 else name + " BOTTOM", fill="white", stroke_width=1, stroke_fill="black", font=font)
    source.save(out / "source.png")
    subprocess.run([ffmpeg, "-v", "error", "-loop", "1", "-i", str(out / "source.png"), "-r", "60", "-frames:v", "320",
                    "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
                    "-x264-params", "aud=1:repeat-headers=1:keyint=60:bframes=0", "-f", "h264", str(out / "source.h264")], check=True)
    bitstream = (out / "source.h264").read_bytes()
    starts = [m.start() for m in re.finditer(b"\x00\x00(?:\x00)?\x01", bitstream) if bitstream[m.end()] & 31 == 9]
    units = [bitstream[start:end] for start, end in zip(starts, starts[1:] + [len(bitstream)])]
    assert len(units) == 320, len(units)
    results = []
    for gpu in (False, True):
        for rotation in (0, 90, 180, 270):
            for mirror in (0, 1):
                name = f"{'gpu' if gpu else 'cpu'}-rot{rotation}-mirror{mirror}"
                case = out / name
                case.mkdir()
                settings = f"cam=0;rot={rotation};mirror={mirror}".encode()
                recording = case / "input.pcm1"
                with recording.open("wb") as file:
                    file.write(HEADER.pack(0x50434D31, 4, 0, 0, len(settings), 0, 0) + settings)
                    for index, unit in enumerate(units):
                        is_key = any(unit[m.end()] & 31 == 5 for m in re.finditer(b"\x00\x00(?:\x00)?\x01", unit))
                        file.write(HEADER.pack(0x50434D31, 2, int(is_key), 0, len(unit), index * 16667, index * 16667) + unit)
                view = None
                with (case / "receiver.log").open("w") as log:
                    process = subprocess.Popen([str(args.receiver), "--slot", str(args.slot), "--no-camera", "--preview",
                                                "--replay", str(recording), "--gpu" if gpu else "--cpu", "--scale-to", args.output_size,
                                                "--dump", str(case / "output.nv12")], stdout=log, stderr=subprocess.STDOUT,
                                               creationflags=subprocess.CREATE_NO_WINDOW)
                    try:
                        deadline = time.monotonic() + 20
                        while process.poll() is None and time.monotonic() < deadline:
                            if view is None:
                                try:
                                    view = Preview(args.slot)
                                except FileNotFoundError:
                                    pass
                            time.sleep(0.002)
                        if process.poll() is None:
                            raise TimeoutError(name)
                        if process.returncode:
                            raise AssertionError(f"{name}: receiver exit {process.returncode}; see {case}")
                        if view is None:
                            raise AssertionError(f"{name}: no preview map")
                        assert view.sequence() > 0
                        (case / "preview.nv12").write_bytes(ctypes.string_at(view.address + 24, 1280 * 720 * 3 // 2))
                    finally:
                        if process.poll() is None:
                            process.terminate()
                            process.wait(timeout=5)
                        if view:
                            view.close()
                receiver_log = (case / "receiver.log").read_text(encoding="utf-8", errors="replace")
                if gpu and "on the GPU" not in receiver_log:
                    raise AssertionError(f"{name}: hardware path unavailable; cannot claim GPU coverage")
                expected = CORNERS[rotation]
                if mirror:
                    expected = expected[1] + expected[0] + expected[3] + expected[2]
                actual = {}
                for channel in ("preview", "output"):
                    width, height = (1280, 720) if channel == "preview" else (output_width, output_height)
                    png = case / f"{channel}.png"
                    subprocess.run([ffmpeg, "-v", "error", "-f", "rawvideo", "-pixel_format", "nv12", "-video_size", f"{width}x{height}",
                                    "-i", str(case / f"{channel}.nv12"), "-frames:v", "1", str(png)], check=True)
                    picture = Image.open(png).convert("RGB")
                    # Sample safely inside each quadrant of the aspect-fitted picture, away from labels/bars.
                    aspect = 360 / 640 if rotation in (90, 270) else 640 / 360
                    content_width, content_height = min(width, height * aspect), min(height, width / aspect)
                    left, top = (width - content_width) / 2, (height - content_height) / 2
                    pixels = [picture.getpixel((int(left + content_width * x), int(top + content_height * y))) for y in (0.25, 0.75) for x in (0.25, 0.75)]
                    actual[channel] = "".join(min(COLORS, key=lambda key: sum((a - b) ** 2 for a, b in zip(pixel, COLORS[key]))) for pixel in pixels)
                result = dict(case=name, output_size=args.output_size, expected=expected, actual=actual, passed=all(value == expected for value in actual.values()))
                results.append(result)
                print(json.dumps(result), flush=True)
    (out / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    failed = [result["case"] for result in results if not result["passed"]]
    if failed:
        raise AssertionError("Incorrect orientation: " + ", ".join(failed))
    print("ROTATION SELF-TEST PASS", flush=True)


if __name__ == "__main__":
    main()
