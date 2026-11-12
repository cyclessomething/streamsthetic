"""PhoneCam spike feeder: phone stream -> in-process H.264 decode -> NV12 -> PhoneCam virtual camera.

Reads the framed phone stream (same protocol as probe.py), decodes each H.264 frame with PyAV
(libavcodec, low-delay, no frame threading) into NV12 at the size the virtual camera publishes,
and writes it into the Global\\PhoneCamFrame shared memory created by the PhoneCam media source
(windows/vcam). Every few seconds it prints:

  decode        = frame received from phone -> decoded frame written to shared memory (PC clock)
  vcam          = frame written -> copied out by the virtual camera (PC clock)
  capture->vcam = phone capture -> copied out by the virtual camera (needs the clock offset)

The virtual camera must be running (VCamSample.exe) and an app must be using it, otherwise the
shared memory does not exist yet; the feeder waits for it.

Needs PyAV (`pip install av`). Run with:  python tools/feeder.py [--host 127.0.0.1 --port 5555] [--seconds 0]
(not with -I: PyAV lives in the user site-packages)
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import socket
import statistics
import struct
import threading
import time

import av

MAGIC = 0x50434D31
HEADER = struct.Struct(">IBBHIqq")
TYPE_CONFIG, TYPE_FRAME, TYPE_CLOCK = 1, 2, 3

SHARED_NAME = "Global\\PhoneCamFrame"
SHARED_MAGIC = 0x31464350
SHARED_SIZE = 64 + 3840 * 2160 * 3 // 2
FILE_MAP_ALL_ACCESS = 0xF001F
FRAME_EVENT_NAME = "Global\\PhoneCamFrameReady"
EVENT_MODIFY_STATE = 0x0002
AV_CODEC_FLAG_LOW_DELAY = 1 << 19


def now_us():
    # perf_counter is QueryPerformanceCounter on Windows, the same clock the media source uses
    return time.perf_counter_ns() // 1000


def recv_exact(sock, n):
    buf = bytearray(n)
    view = memoryview(buf)
    got = 0
    while got < n:
        r = sock.recv_into(view[got:], n - got)
        if r == 0:
            raise ConnectionError("stream closed")
        got += r
    return buf


def open_shared():
    """Opens (never creates) the mapping the media source created. Returns a writable memoryview."""
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenFileMappingW.restype = wt.HANDLE
    k32.OpenFileMappingW.argtypes = [wt.DWORD, wt.BOOL, wt.LPCWSTR]
    k32.MapViewOfFile.restype = ctypes.c_void_p
    k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
    handle = k32.OpenFileMappingW(FILE_MAP_ALL_ACCESS, False, SHARED_NAME)
    if not handle:
        return None
    addr = k32.MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, SHARED_SIZE)
    if not addr:
        raise OSError(ctypes.get_last_error(), "MapViewOfFile failed")
    return memoryview((ctypes.c_char * SHARED_SIZE).from_address(addr)).cast("B")


def open_frame_event():
    """Returns a callable that signals the media source's frame-ready event (a no-op if it is missing)."""
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenEventW.restype = wt.HANDLE
    k32.OpenEventW.argtypes = [wt.DWORD, wt.BOOL, wt.LPCWSTR]
    k32.SetEvent.argtypes = [wt.HANDLE]
    handle = k32.OpenEventW(EVENT_MODIFY_STATE, False, FRAME_EVENT_NAME)
    if not handle:
        print("frame-ready event not found; virtual camera falls back to polling", flush=True)
        return lambda: None
    return lambda: k32.SetEvent(handle)


def median(values):
    return statistics.median(values) if values else float("nan")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5555)
    ap.add_argument("--seconds", type=float, default=0, help="0 = run until stopped")
    ap.add_argument("--code", default="", help="6-digit pairing code shown on the phone (Wi-Fi/tethering only)")
    args = ap.parse_args()

    print("waiting for the PhoneCam virtual camera shared memory (start VCamSample.exe and open the camera in an app)...",
          flush=True)
    shared = None
    while shared is None:
        shared = open_shared()
        if shared is None:
            time.sleep(0.5)
    while True:
        magic, _, width, height = struct.unpack_from("<IIII", shared, 0)
        if magic == SHARED_MAGIC and width and height:
            break
        time.sleep(0.2)
    frame_bytes = width * height * 3 // 2
    print(f"virtual camera serves {width}x{height} NV12", flush=True)
    signal_frame = open_frame_event()

    # one frame in, one frame out: low-delay flag and no frame threading (frame threads hold frames back)
    decoder = av.CodecContext.create("h264", "r")
    decoder.flags |= AV_CODEC_FLAG_LOW_DELAY
    decoder.thread_count = 1

    sock = socket.create_connection((args.host, args.port))
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    if args.code:
        sock.sendall(b"P" + args.code.encode("ascii"))
    stop = threading.Event()
    clock_samples = []  # (rtt_us, offset_us); phone_time ~= pc_time + offset
    written = {}  # seq -> (ptsUs, writeUs)
    stats = {"decode": [], "vcam": [], "capture_vcam": [], "frames": 0}
    seq = 0
    last_read_seq = 0
    synced = False
    skipped = 0

    def clock_requester():
        sent = 0
        while not stop.is_set():
            try:
                sock.sendall(b"T" + struct.pack(">q", now_us()))
            except OSError:
                return
            sent += 1
            stop.wait(0.2 if sent < 20 else 1.0)

    threading.Thread(target=clock_requester, daemon=True).start()

    t_end = time.time() + args.seconds if args.seconds else float("inf")
    next_report = time.time() + 5
    try:
        while time.time() < t_end:
            header = recv_exact(sock, HEADER.size)
            rx = now_us()
            magic, ptype, flags, _, length, pts, send_us = HEADER.unpack(header)
            if magic != MAGIC:
                raise ValueError(f"bad magic {magic:#x}")
            payload = recv_exact(sock, length) if length else b""
            if ptype == TYPE_CLOCK:
                rtt = rx - send_us
                clock_samples.append((rtt, pts - (send_us + rtt / 2)))
            elif ptype in (TYPE_CONFIG, TYPE_FRAME):
                # joining mid-stream: frames before the first codec config or keyframe cannot be decoded
                if not synced:
                    if ptype == TYPE_FRAME and not flags & 1:
                        continue
                    synced = True
                try:
                    frames = decoder.decode(av.Packet(bytes(payload)))
                except av.error.InvalidDataError:
                    skipped += 1
                    continue
                for frame in frames:
                    nv12 = frame.reformat(width=width, height=height, format="nv12").to_ndarray()
                    seq += 1
                    struct.pack_into("<q", shared, 16, seq * 2 - 1)  # odd: writing
                    shared[64:64 + frame_bytes] = nv12.reshape(-1).data
                    write_us = now_us()
                    struct.pack_into("<qq", shared, 24, pts, write_us)
                    struct.pack_into("<q", shared, 16, seq * 2)  # even: complete
                    signal_frame()
                    written[seq * 2] = (pts, write_us)
                    if ptype == TYPE_FRAME:
                        stats["decode"].append((write_us - rx) / 1000)
                        stats["frames"] += 1

                # how fresh were frames when the virtual camera picked them up
                read_us, read_seq = struct.unpack_from("<qq", shared, 40)
                if read_seq != last_read_seq and read_seq in written:
                    last_read_seq = read_seq
                    w_pts, w_us = written[read_seq]
                    stats["vcam"].append((read_us - w_us) / 1000)
                    if clock_samples:
                        offset = min(clock_samples)[1]
                        stats["capture_vcam"].append((read_us - (w_pts - offset)) / 1000)
                for old in [s for s in written if s < seq * 2 - 240]:
                    del written[old]

            if time.time() >= next_report:
                next_report = time.time() + 5
                print(f"{stats['frames'] / 5:5.1f} fps written | decode median {median(stats['decode']):6.1f} ms | "
                      f"written->vcam median {median(stats['vcam']):6.1f} ms (n={len(stats['vcam'])}) | "
                      f"capture->vcam median {median(stats['capture_vcam']):6.1f} ms | skipped {skipped}", flush=True)
                stats.update(decode=[], vcam=[], capture_vcam=[], frames=0)
    finally:
        stop.set()
        sock.close()


if __name__ == "__main__":
    main()
