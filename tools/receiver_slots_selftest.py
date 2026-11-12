"""Exercise four real receiver processes without registering or opening a camera.

Run after building the receiver: python tools/receiver_slots_selftest.py
Requires Windows and the local PCM1 recording (override paths with --receiver/--recording).
Leaves installed cameras and phone connections untouched. Fails if a slot is already in use.
"""
import argparse
import ctypes
from ctypes import wintypes
from pathlib import Path
import struct
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.OpenFileMappingW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel.OpenFileMappingW.restype = wintypes.HANDLE
kernel.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
kernel.MapViewOfFile.restype = ctypes.c_void_p
kernel.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
HEADER = struct.Struct("<III4xq")


class Preview:
    def __init__(self, slot):
        name = "Local\\PhoneCamPreview" + (str(slot) if slot > 1 else "")
        self.handle = kernel.OpenFileMappingW(4, False, name)  # FILE_MAP_READ; never creates a missing map
        self.address = kernel.MapViewOfFile(self.handle, 4, 0, 0, 0) if self.handle else None
        if not self.address:
            if self.handle:
                kernel.CloseHandle(self.handle)
            raise FileNotFoundError(name)

    def sequence(self):
        magic, width, height, seq = HEADER.unpack(ctypes.string_at(self.address, HEADER.size))
        assert (magic, width, height) == (0x50435056, 1280, 720), (magic, width, height)
        return seq

    def close(self):
        kernel.UnmapViewOfFile(self.address)
        kernel.CloseHandle(self.handle)


def wait_until(check, description, seconds=8):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(0.05)
    raise AssertionError("Timed out: " + description)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receiver", type=Path, default=ROOT / "windows/receiver/x64/Release/StreamstheticReceiver.exe")
    parser.add_argument("--recording", type=Path, default=ROOT / "tools/testdata/phone-1080p60-10s.pcm1")
    args = parser.parse_args()
    assert args.receiver.is_file(), f"Build receiver first: {args.receiver}"
    assert args.recording.is_file(), f"Recording missing: {args.recording}"
    for value in ("0", "5", "-1", "2garbage", ""):
        result = subprocess.run([str(args.receiver), "--slot", value, "--list-broadcasts"], capture_output=True, text=True, timeout=5)
        assert result.returncode != 0 and "--slot expects" in result.stderr, (value, result)
    print("PASS: invalid slots rejected")

    processes, previews, logs = {}, {}, {}
    with tempfile.TemporaryDirectory(prefix="streamsthetic-slots-") as directory:
        try:
            def start(slot):
                log = open(Path(directory) / f"slot-{slot}-{len(logs)}.log", "w+")
                logs[len(logs)] = log
                command = [str(args.receiver), "--slot", str(slot), "--replay", str(args.recording),
                           "--realtime", "--no-camera", "--preview"]
                process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                           creationflags=subprocess.CREATE_NO_WINDOW)
                processes[slot] = process
                return process

            for slot in range(1, 5):
                start(slot)

            def open_previews():
                for slot, process in processes.items():
                    assert process.poll() is None, f"Receiver slot {slot} stopped; check whether another receiver owns it"
                    if slot not in previews:
                        try:
                            previews[slot] = Preview(slot)
                        except FileNotFoundError:
                            pass
                return len(previews) == 4 and all(p.sequence() > 0 for p in previews.values())

            wait_until(open_previews, "four preview maps with frames")
            initial = {slot: p.sequence() for slot, p in previews.items()}
            wait_until(lambda: all(p.sequence() > initial[slot] for slot, p in previews.items()), "all four streams progressing")
            print("PASS: four simultaneous receivers publish to four distinct previews (legacy slot 1 preserved)")

            duplicate = subprocess.run([str(args.receiver), "--slot", "2", "--replay", str(args.recording), "--no-camera"],
                                       capture_output=True, text=True, timeout=5)
            assert duplicate.returncode != 0 and "already has a receiver" in duplicate.stderr, duplicate
            print("PASS: duplicate writer rejected")

            processes[2].terminate()
            processes[2].wait(timeout=5)
            stopped = previews[2].sequence()
            initial = {slot: p.sequence() for slot, p in previews.items()}
            wait_until(lambda: all(previews[s].sequence() > initial[s] + 2 for s in (1, 3, 4)), "remaining streams continue")
            assert previews[2].sequence() == stopped, "Another receiver wrote to stopped slot 2"
            print("PASS: stopping slot 2 leaves slots 1, 3 and 4 streaming and its own buffer unchanged")

            # Keep the mapping open: the new writer must recover while a reader still holds the old buffer.
            start(2)
            wait_until(lambda: previews[2].sequence() != stopped and previews[2].sequence() > 0, "slot 2 restart")
            print("PASS: slot can restart after its writer exits")
        finally:
            for process in processes.values():
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=5)
            for preview in previews.values():
                preview.close()
            for log in logs.values():
                log.seek(0)
                output = log.read()
                if "error:" in output:
                    print(output)
                log.close()
    print("RECEIVER SLOTS SELF-TEST PASS")


if __name__ == "__main__":
    main()
