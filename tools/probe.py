"""PhoneCam latency probe.

Connects to the phone stream (`adb forward tcp:5555 localabstract:phonecam`), estimates the
phone/PC clock offset with NTP-style ping-pongs, and reports per frame:

  A = capture -> encoder output ready   (phone clock only, no sync needed)
  B = encoder output ready -> received on the PC (needs the clock offset)

With --relay-port the raw Annex-B video is also served on localhost for a player, e.g.
  mpv --profile=low-latency --untimed --no-cache --demuxer-lavf-format=h264 tcp://127.0.0.1:5556

Run with:  python -I tools/probe.py --seconds 30 [--relay-port 5556] [--csv out.csv]
"""
import argparse
import collections
import csv
import socket
import statistics
import struct
import threading
import time

MAGIC = 0x50434D31
HEADER = struct.Struct(">IBBHIqq")  # magic, type, flags, reserved, length, ptsUs, sendUs
TYPE_CONFIG, TYPE_FRAME, TYPE_CLOCK = 1, 2, 3


def now_us():
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


class Relay:
    """Serves the raw Annex-B stream to a local player without ever blocking the receive loop."""

    def __init__(self, port):
        self.server = socket.create_server(("127.0.0.1", port))
        self.queue = collections.deque(maxlen=240)
        self.cond = threading.Condition()
        self.config = None
        self.client = None
        threading.Thread(target=self._accept, daemon=True).start()
        threading.Thread(target=self._pump, daemon=True).start()

    def _accept(self):
        while True:
            conn, _ = self.server.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self.cond:
                self.queue.clear()
                if self.config:
                    self.queue.append(self.config)
                self.client = conn
                self.cond.notify()

    def _pump(self):
        while True:
            with self.cond:
                while not (self.client and self.queue):
                    self.cond.wait()
                data = self.queue.popleft()
                conn = self.client
            try:
                conn.sendall(data)
            except OSError:
                with self.cond:
                    self.client = None

    def push(self, data, is_config=False):
        with self.cond:
            if is_config:
                self.config = bytes(data)
            self.queue.append(bytes(data))
            self.cond.notify()


def pct(values, p):
    values = sorted(values)
    return values[min(len(values) - 1, int(len(values) * p))]


def summarize(name, ms):
    if not ms:
        return f"{name}: no data"
    return (f"{name}: min {min(ms):7.2f}  median {statistics.median(ms):7.2f}  "
            f"p95 {pct(ms, 0.95):7.2f}  p99 {pct(ms, 0.99):7.2f}  max {max(ms):7.2f}  ms")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5555)
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--relay-port", type=int, default=0)
    ap.add_argument("--csv", default="")
    ap.add_argument("--code", default="", help="6-digit pairing code shown on the phone (Wi-Fi/tethering only)")
    args = ap.parse_args()

    relay = Relay(args.relay_port) if args.relay_port else None
    sock = socket.create_connection((args.host, args.port))
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    if args.code:
        sock.sendall(b"P" + args.code.encode("ascii"))
    stop = threading.Event()
    clock_samples = []  # (rtt_us, offset_us); phone_time ~= pc_time + offset

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

    frames = []  # (ptsUs, sendUs, rxUs, size, key)
    t_end = time.time() + args.seconds
    try:
        while time.time() < t_end:
            header = recv_exact(sock, HEADER.size)
            rx_us = now_us()
            magic, ptype, flags, _, length, pts, send_us = HEADER.unpack(header)
            if magic != MAGIC:
                raise ValueError(f"bad magic {magic:#x}")
            payload = recv_exact(sock, length) if length else b""
            if ptype == TYPE_CLOCK:
                t0, tp = send_us, pts
                rtt = rx_us - t0
                clock_samples.append((rtt, tp - (t0 + rtt / 2)))
            elif ptype == TYPE_CONFIG:
                if relay:
                    relay.push(payload, is_config=True)
            elif ptype == TYPE_FRAME:
                frames.append((pts, send_us, now_us(), length, flags & 1))
                if relay:
                    relay.push(payload)
    finally:
        stop.set()
        sock.close()

    if not frames or not clock_samples:
        print("no frames or no clock samples received")
        return
    rtt_min, offset = min(clock_samples)
    print(f"frames: {len(frames)} in {args.seconds:.0f}s ({len(frames) / args.seconds:.1f} fps), "
          f"clock samples: {len(clock_samples)}, best RTT {rtt_min / 1000:.2f} ms "
          f"(offset uncertainty about +/-{rtt_min / 2000:.2f} ms)")

    a_ms = [(s - p) / 1000 for p, s, r, n, k in frames]
    b_ms = [(r - (s - offset)) / 1000 for p, s, r, n, k in frames]
    ab_ms = [a + b for a, b in zip(a_ms, b_ms)]
    pts_gap = [(frames[i][0] - frames[i - 1][0]) / 1000 for i in range(1, len(frames))]
    rx_gap = [(frames[i][2] - frames[i - 1][2]) / 1000 for i in range(1, len(frames))]
    sizes_kb = [n / 1024 for p, s, r, n, k in frames]
    print(summarize("A capture->encoded (phone)      ", a_ms))
    print(summarize("B encoded->received (transport) ", b_ms))
    print(summarize("A+B capture->PC received        ", ab_ms))
    print(summarize("capture interval (phone)        ", pts_gap))
    print(summarize("receive interval (PC)           ", rx_gap))
    print(f"frame size KB: median {statistics.median(sizes_kb):.1f}  max {max(sizes_kb):.1f}  "
          f"keyframes: {sum(f[4] for f in frames)}")

    if args.csv:
        with open(args.csv, "w", newline="") as fh:
            w = csv.writer(fh)
            w.writerow(["pts_us", "send_us", "rx_us", "size", "key", "A_ms", "B_ms"])
            for f, a, b in zip(frames, a_ms, b_ms):
                w.writerow([*f, f"{a:.3f}", f"{b:.3f}"])


if __name__ == "__main__":
    main()
