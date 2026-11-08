#!/usr/bin/env python3
"""Extract the HECATE (or any) vendor-channel timeline: OUT writes + IN reads,
with timestamps, deduped and paired."""
import struct
import sys
from collections import Counter

EPB = 0x00000006


def blocks(buf):
    off = 0
    while off + 12 <= len(buf):
        btype, blen = struct.unpack_from("<II", buf, off)
        if blen < 12 or off + blen > len(buf):
            break
        yield btype, buf[off + 8:off + blen - 4]
        off += blen


def main():
    path = sys.argv[1]
    ep_out = int(sys.argv[2], 0)
    ep_in = int(sys.argv[3], 0)
    dev = sys.argv[4] if len(sys.argv) > 4 else None
    buf = open(path, "rb").read()
    t0 = None
    rows = []
    for btype, body in blocks(buf):
        if btype != EPB:
            continue
        iface, ts_hi, ts_lo, cap, orig = struct.unpack_from("<IIIII", body, 0)
        ts = (ts_hi << 32) | ts_lo
        data = body[20:20 + cap]
        if len(data) < 27:
            continue
        hlen, irp, status, func, info, bus, d, ep, transfer, dlen = \
            struct.unpack_from("<HQIHBHHBBI", data, 0)
        if dev and f"{bus}:{d}" != dev:
            continue
        if ep not in (ep_out, ep_in):
            continue
        payload = data[hlen:hlen + dlen]
        if not payload:
            continue
        if t0 is None:
            t0 = ts
        comp = bool(info & 1)
        rows.append((ts - t0, ep, comp, bytes(payload)))

    # timestamp units: USBPcap uses ... assume ns if huge else us
    print(f"# {len(rows)} payload-bearing packets on ep {ep_out:#x}/{ep_in:#x}")
    print("t(ms)  ep  dir  len  frame")
    seen = Counter()
    for dt, ep, comp, p in rows:
        ms = dt / 1e6 if dt > 1e12 else dt / 1e3
        h = p.hex(' ')
        print(f"{ms:8.1f} {ep:#04x} {'IN' if ep == ep_in else 'OUT':3} {len(p):3}  {h}")
        seen[p] += 1


if __name__ == "__main__":
    main()
