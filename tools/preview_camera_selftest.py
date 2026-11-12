"""Recorded 4K60 input -> fast preview and installed 720p60 camera concurrently.

Does not install cameras or connect to a phone. Use an unused slot. Only processes
started by this script are stopped, and no video is saved (frame hashes only).
"""
import argparse
import json
from pathlib import Path
import re
import subprocess


ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--receiver', type=Path, default=ROOT / 'windows/receiver/x64/Release/StreamstheticReceiver.exe')
    parser.add_argument('--slot', type=int, choices=range(1, 5), default=4)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    recording = ROOT / 'tools/testdata/phone-4k60-60mbps-10s.pcm1'
    camera = 'Streamsthetic' + (f' {args.slot}' if args.slot > 1 else '') + ' (Windows Virtual Camera)'
    receiver = reader = None
    hidden = subprocess.CREATE_NO_WINDOW
    with (args.output / 'receiver.log').open('w') as receiver_log, \
         (args.output / 'camera.log').open('w') as camera_log, \
         (args.output / 'camera.framehash').open('w') as hashes:
        try:
            receiver = subprocess.Popen([str(args.receiver), '--slot', str(args.slot), '--replay', str(recording),
                                         '--realtime', '--no-register', '--preview'], stdout=receiver_log,
                                        stderr=subprocess.STDOUT, creationflags=hidden)
            # The Windows DirectShow bridge rejects literal60;59.94 selects its advertised nominal60 mode.
            reader = subprocess.Popen(['ffmpeg', '-hide_banner', '-loglevel', 'info', '-f', 'dshow',
                                       '-video_size', '1280x720', '-framerate', '59.94', '-i', 'video=' + camera,
                                       '-t', '5', '-an', '-fps_mode', 'passthrough', '-f', 'framehash', '-'],
                                      stdout=hashes, stderr=camera_log, creationflags=hidden)
            reader.wait(timeout=25)
            receiver.wait(timeout=15)
        finally:
            for process in (reader, receiver):
                if process is not None and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
    camera_text = (args.output / 'camera.log').read_text()
    receiver_text = (args.output / 'receiver.log').read_text()
    rows = [line.split(',') for line in (args.output / 'camera.framehash').read_text().splitlines()
            if line and not line.startswith('#')]
    timestamps = [int(row[1]) for row in rows]
    result = dict(frames=len(rows), distinct_frames=len({row[-1].strip() for row in rows}),
                  receiver_exit=receiver.returncode, camera_exit=reader.returncode,
                  monotonic=all(b > a for a, b in zip(timestamps, timestamps[1:])))
    (args.output / 'result.json').write_text(json.dumps(result, indent=2))
    assert receiver.returncode == reader.returncode == 0, 'Receiver/camera process failed'
    assert 295 <= len(rows) <= 305, f'Camera missed60fps budget: {len(rows)} frames/5seconds'
    assert result['distinct_frames'] >= 250, 'Camera froze or repeated too many pictures'
    assert result['monotonic'] and not re.search(r'non.?monoton|invalid.*(?:dts|timestamp)', camera_text, re.I)
    assert re.search(r'written to camera [1-9][0-9]*', receiver_text), 'No frames written'
    print(json.dumps(result))
    print('PREVIEW + CAMERA SELF-TEST PASS')


if __name__ == '__main__':
    main()
