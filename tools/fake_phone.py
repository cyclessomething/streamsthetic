"""Fake phone for offline tests: serves a recorded PCM1 stream over TCP the way the phone app does.

- Real-time pacing from the recorded capture timestamps; loops the recording.
- Optional pairing code ('P' + 6 digits, like the app's Wi-Fi mode).
- Answers clock requests ('T' + token) on its own clock, and rewrites each frame's timestamps onto that clock
  while keeping the recorded capture->encoded delay, so receivers can compute a meaningful capture->camera latency.
- --drop-after closes every connection after N seconds to exercise reconnect logic.

Run with:  python tools/fake_phone.py --file tools/testdata/phone-1080p60-10s.pcm1 [--port 5599] [--code 123456]
                                      [--drop-after 6] [--seconds 60]
"""
import argparse
import socket
import struct
import threading
import time

MAGIC = 0x50434D31
HEADER = struct.Struct(">IBBHIqq")
TYPE_CONFIG, TYPE_FRAME, TYPE_CLOCK, TYPE_SETTINGS = 1, 2, 3, 4
# what the fake phone claims to stream; changed by 'S' messages like the real app (video stays the recording)
SETTINGS = {"cam": "0", "zoom": "1.00", "w": "1920", "h": "1080", "fps": "60", "mbps": "20"}
PHONE_CLOCK_OFFSET_US = 123_456_789  # the fake phone's clock runs ahead of the PC's by this much


def phone_now_us():
    return time.perf_counter_ns() // 1000 + PHONE_CLOCK_OFFSET_US


def recv_exact(conn, n):
    """socket.MSG_WAITALL is not supported on Windows sockets (WinError 10045), so loop."""
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return buf
        buf += chunk
    return buf


def load_packets(path):
    packets = []
    with open(path, "rb") as f:
        data = f.read()
    pos = 0
    while pos + HEADER.size <= len(data):
        magic, ptype, flags, _, length, pts, send = HEADER.unpack_from(data, pos)
        if magic != MAGIC:
            raise ValueError(f"bad magic at offset {pos}")
        payload = data[pos + HEADER.size:pos + HEADER.size + length]
        if len(payload) < length:
            break  # recording cut mid-packet
        pos += HEADER.size + length
        if ptype in (TYPE_CONFIG, TYPE_FRAME):
            packets.append((ptype, flags, pts, send, payload))
    return packets


def serve_client(conn, packets, args):
    lock = threading.Lock()
    stop = threading.Event()

    def send(ptype, flags, pts, send_us, payload=b""):
        with lock:
            conn.sendall(HEADER.pack(MAGIC, ptype, flags, 0, len(payload), pts, send_us) + payload)

    def send_settings():
        send(TYPE_SETTINGS, 0, 0, phone_now_us(), ";".join(f"{k}={v}" for k, v in SETTINGS.items()).encode())

    def answer_clock():
        # 'T' + i64 token: clock request; 'S' + u16 length + "key=value;...": settings change
        try:
            while not stop.is_set():
                kind = recv_exact(conn, 1)
                if len(kind) < 1:
                    return
                if kind == b"T":
                    req = recv_exact(conn, 8)
                    if len(req) < 8:
                        return
                    send(TYPE_CLOCK, 0, phone_now_us(), struct.unpack(">q", req)[0])
                elif kind == b"S":
                    length = struct.unpack(">H", recv_exact(conn, 2))[0]
                    text = recv_exact(conn, length).decode("ascii")
                    print(f"settings from PC: {text}", flush=True)
                    for pair in text.split(";"):
                        key, _, value = pair.partition("=")
                        if key.strip() in SETTINGS:
                            SETTINGS[key.strip()] = value.strip()
                    send_settings()
        except OSError:
            pass

    try:
        if args.code:
            conn.settimeout(5)
            hello = recv_exact(conn, 7)
            conn.settimeout(None)
            if hello != b"P" + args.code.encode():
                print("rejected client: wrong pairing code", flush=True)
                return
        threading.Thread(target=answer_clock, daemon=True).start()
        send_settings()

        start = time.perf_counter()
        frames = 0
        # start at the first keyframe like a phone that was already streaming
        first_key = next(i for i, p in enumerate(packets) if p[0] == TYPE_FRAME and p[1] & 1)
        order = [p for p in packets if p[0] == TYPE_CONFIG][:1] + packets[first_key:]
        while True:
            base = time.perf_counter()
            first_pts = None
            for ptype, flags, pts, send_us, payload in order:
                if args.drop_after and time.perf_counter() - start >= args.drop_after:
                    print(f"dropping connection after {args.drop_after}s ({frames} frames)", flush=True)
                    return
                if args.corrupt_after and time.perf_counter() - start >= args.corrupt_after:
                    print(f"sending garbage after {args.corrupt_after}s ({frames} frames)", flush=True)
                    with lock:
                        conn.sendall(b"\xde\xad\xbe\xef" * 64)
                    time.sleep(1)
                    return
                if ptype == TYPE_CONFIG:
                    send(TYPE_CONFIG, 0, 0, phone_now_us(), payload)
                    continue
                if first_pts is None:
                    first_pts = pts
                due = base + (pts - first_pts) / 1e6
                while time.perf_counter() < due:
                    time.sleep(0.0005)
                now = phone_now_us()
                send(TYPE_FRAME, flags, now - (send_us - pts), now, payload)
                frames += 1
            order = [p for p in packets if p[0] == TYPE_FRAME]  # loop: later passes skip the config packet
    except OSError as e:
        print(f"client gone: {e}", flush=True)
    finally:
        stop.set()
        conn.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", required=True)
    ap.add_argument("--port", type=int, default=5599)
    ap.add_argument("--code", default="")
    ap.add_argument("--drop-after", type=float, default=0)
    ap.add_argument("--corrupt-after", type=float, default=0, help="send garbage (breaks the stream) after N seconds")
    ap.add_argument("--seconds", type=float, default=60, help="stop serving after this long")
    args = ap.parse_args()

    packets = load_packets(args.file)
    print(f"loaded {sum(p[0] == TYPE_FRAME for p in packets)} frames; listening on 127.0.0.1:{args.port}", flush=True)

    # answer Wi-Fi discovery queries like the app (UDP 5557, loopback only so no firewall prompt)
    def discovery():
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.bind(("127.0.0.1", 5557))
        udp.settimeout(0.5)
        end = time.time() + args.seconds
        while time.time() < end:
            try:
                data, addr = udp.recvfrom(64)
            except socket.timeout:
                continue
            if data.startswith(b"PHONECAM?"):
                udp.sendto(f"PHONECAM1|FakePhone|{args.port}".encode(), addr)
                print(f"answered discovery from {addr[0]}", flush=True)
        udp.close()

    threading.Thread(target=discovery, daemon=True).start()
    server = socket.create_server(("127.0.0.1", args.port))
    server.settimeout(0.5)
    end = time.time() + args.seconds
    while time.time() < end:
        try:
            conn, addr = server.accept()
        except socket.timeout:
            continue
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        print(f"client connected from {addr[0]}", flush=True)
        threading.Thread(target=serve_client, args=(conn, packets, args), daemon=True).start()
    server.close()


if __name__ == "__main__":
    main()
