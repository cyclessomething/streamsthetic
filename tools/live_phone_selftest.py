"""Opt-in device acceptance: real phone -> receiver -> installed Windows camera.

Requires explicit --serial and installed camera slots. Starts the phone app, changes
only bitrate/mirror briefly, restores both, and restores its initial streaming state.
No video is saved. Logs go to --output. Run only with the phone owner's permission.
"""
import argparse
import pathlib
import queue
import socket
import subprocess
import threading
import time


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--serial', required=True)
    p.add_argument('--receiver', required=True)
    p.add_argument('--slot', type=int, choices=range(1, 5), default=4)
    p.add_argument('--output', required=True)
    args = p.parse_args()
    out = pathlib.Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    def adb(*command):
        return subprocess.check_output(['adb', '-s', args.serial, *command], timeout=30)
    was_streaming = b'CameraStreamService' in adb('shell', 'dumpsys', 'activity', 'services', 'com.phonecam')
    saved_preferences = None if was_streaming else adb('exec-out', 'run-as', 'com.phonecam', 'cat', 'shared_prefs/settings.xml')
    receiver = reader = None
    baseline = None
    latest = {}
    restored = False
    forwarded = False
    lines = []
    events = queue.Queue()
    with socket.socket() as temporary:
        temporary.bind(('127.0.0.1', 0))
        port = temporary.getsockname()[1]

    def wait_for(check, seconds, description):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                line = events.get(timeout=.2)
                lines.append(line)
                if line.startswith('phone settings: '):
                    latest.clear()
                    latest.update(pair.split('=', 1) for pair in line[len('phone settings: '):].strip().split(';') if '=' in pair)
            except queue.Empty:
                pass
            if check():
                print('PASS:', description, flush=True)
                return
        raise RuntimeError('Timed out: ' + description)

    def send(settings):
        receiver.stdin.write('set ' + settings + '\n')
        receiver.stdin.flush()

    try:
        if not was_streaming:
            adb('shell', 'am', 'start', '-n', 'com.phonecam/.MainActivity', '--ez', 'autostart', 'true')
            time.sleep(3)
        adb('forward', f'tcp:{port}', 'localabstract:phonecam')
        forwarded = True
        receiver = subprocess.Popen([args.receiver, '--slot', str(args.slot), '--host', '127.0.0.1', '--port', str(port),
                                     '--no-adb', '--no-register', '--preview', '--seconds', '70'],
                                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding='utf-8', errors='replace')
        def collect():
            for line in receiver.stdout:
                events.put(line)
        threading.Thread(target=collect, daemon=True).start()
        wait_for(lambda: 'mbps' in latest and 'mirror' in latest, 15, 'real phone connected and reported its settings')
        baseline = {key: latest[key] for key in ('mbps', 'mirror')}
        changed = {'mbps': '10' if baseline['mbps'] != '10' else '20', 'mirror': '0' if baseline['mirror'] == '1' else '1'}
        send(';'.join(f'{k}={v}' for k, v in changed.items()))
        wait_for(lambda: all(latest.get(k) == v for k, v in changed.items()), 10, 'real phone applied bitrate and mirror commands')
        send(';'.join(f'{k}={v}' for k, v in baseline.items()))
        wait_for(lambda: all(latest.get(k) == v for k, v in baseline.items()), 10, 'original bitrate and mirror restored')
        restored = True
        name = 'Streamsthetic' + (f' {args.slot}' if args.slot > 1 else '') + ' (Windows Virtual Camera)'
        reader = subprocess.Popen(['ffmpeg', '-hide_banner', '-loglevel', 'info', '-f', 'dshow', '-video_size', '1280x720',
                                   '-framerate', '30', '-i', 'video=' + name, '-t', '8', '-f', 'null', '-'],
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        _, errors = reader.communicate(timeout=25)
        text = errors.decode('utf-8', errors='replace')
        (out / 'camera-reader.log').write_text(text, encoding='utf-8')
        import re
        frames = [int(n) for n in re.findall(r'frame=\s*(\d+)', text)]
        if (reader.returncode != 0 or not frames or not 230 <= max(frames) <= 250
                or 'non monotonically' in text):
            raise RuntimeError('Installed camera did not deliver the expected frames; inspect camera-reader.log')
        print(f'PASS: installed camera {args.slot} delivered {max(frames)} frames at requested 720p30', flush=True)
        wait_for(lambda: any('decoded' in line and 'fps' in line for line in lines), 6, 'receiver produced live decoding statistics')
        print('LIVE PHONE SELF-TEST PASS', flush=True)
    finally:
        if reader and reader.poll() is None:
            reader.kill()
            reader.wait(timeout=5)
        try:
            if baseline and not restored and receiver and receiver.poll() is None:
                send(';'.join(f'{k}={v}' for k, v in baseline.items()))
                wait_for(lambda: all(latest.get(k) == v for k, v in baseline.items()), 10, 'original settings restored during cleanup')
        finally:
            if receiver and receiver.poll() is None:
                receiver.terminate()
                receiver.wait(timeout=5)
            while not events.empty():
                lines.append(events.get_nowait())
            (out / 'receiver.log').write_text(''.join(lines), encoding='utf-8')
            try:
                if forwarded:
                    adb('forward', '--remove', f'tcp:{port}')
            finally:
                if not was_streaming:
                    adb('shell', 'am', 'force-stop', 'com.phonecam')
                    # Starting the activity selects its streaming tab. Restore the exact original
                    # settings only after stopping it; the new installation identity is a separate file.
                    if saved_preferences is not None:
                        local = out / 'restore-settings.xml'
                        remote = f'/data/local/tmp/streamsthetic-test-{port}.xml'
                        local.write_bytes(saved_preferences)
                        adb('push', str(local), remote)
                        adb('shell', 'chmod', '644', remote)
                        adb('shell', 'run-as', 'com.phonecam', 'cp', remote, 'shared_prefs/settings.xml')
                        if adb('exec-out', 'run-as', 'com.phonecam', 'cat', 'shared_prefs/settings.xml') != saved_preferences:
                            raise RuntimeError('Phone preferences did not restore; backup retained for recovery')
                        adb('shell', 'rm', remote)
                        local.unlink()
                        print('PASS: phone preferences restored byte-for-byte and streaming stopped', flush=True)


if __name__ == '__main__':
    main()
