#!/usr/bin/env python3
"""Minimal pcapng + USBPcap reader.

No external deps: parses blocks manually, decodes USBPcap headers and
USB setup packets, prints a readable timeline.

Usage:
  python3 usbpcap_dump.py <file.pcapng> [--hex] [--dev BUS:DEV] [--ep EP]
"""
import struct
import sys
import argparse

SHB = 0x0A0D0D0A
IDB = 0x00000001
EPB = 0x00000006
SPB = 0x00000003

TRANSFER = {0: "ISO", 1: "INT", 2: "CTL", 3: "BULK", 0xFF: "?"}

# bmRequestType
def req_type(v):
    d = "D→H" if v & 0x80 else "H→D"
    t = {0: "STD", 1: "CLASS", 2: "VENDOR", 3: "RESV"}[(v >> 5) & 3]
    r = {0: "DEV", 1: "IF", 2: "EP", 3: "OTHER"}[v & 3]
    return f"{d} {t}/{r}"

REQUEST = {
    (0x21, 0x09): "SET_REPORT",
    (0xA1, 0x01): "GET_REPORT",
    (0xA1, 0x81): "GET_DESCRIPTOR*",
    (0x81, 0x06): "GET_DESCRIPTOR",
    (0x21, 0x0A): "SET_IDLE",
    (0x21, 0x0B): "SET_PROTOCOL",
    (0x21, 0x0A): "SET_IDLE",
}


def parse_blocks(buf):
    off = 0
    while off + 12 <= len(buf):
        btype, blen = struct.unpack_from("<II", buf, off)
        if blen < 12 or off + blen > len(buf):
            break
        body = buf[off + 8:off + blen - 4]
        yield btype, body
        off += blen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--hex", action="store_true")
    ap.add_argument("--dev")
    ap.add_argument("--ep", type=int)
    ap.add_argument("--sum", action="store_true", help="only summary counts")
    a = ap.parse_args()

    buf = open(a.path, "rb").read()
    interfaces = []
    packet_no = 0
    lines = []
    devs = {}

    for btype, body in parse_blocks(buf):
        if btype == IDB:
            linktype = struct.unpack_from("<H", body, 0)[0]
            interfaces.append(linktype)
        elif btype == EPB:
            iface, ts_hi, ts_lo, cap, orig = struct.unpack_from("<IIIII", body, 0)
            data = body[20:20 + cap]
            packet_no += 1
            if len(data) < 27:
                continue
            hlen, irp, status, func, info, bus, dev, ep, transfer, dlen = \
                struct.unpack_from("<HQIHBHHBBI", data, 0)
            payload = data[hlen:hlen + dlen]
            key = (bus, dev)
            devs.setdefault(key, 0)
            devs[key] += 1
            if a.dev and f"{bus}:{dev}" != a.dev:
                continue
            if a.ep is not None and ep != a.ep:
                continue
            complete = bool(info & 0x01)
            tag = f"#{packet_no} {bus}:{dev} ep=0x{ep:02x} {TRANSFER.get(transfer,hex(transfer))} " \
                  f"{'IN ' if complete else 'OUT'} st=0x{status:08x}"
            if transfer == 2 and len(payload) >= 8:
                bm, br, wv, wi, wl = struct.unpack_from("<BBHHH", payload, 0)
                name = REQUEST.get((bm, br), "")
                tag += f" {req_type(bm)} bRequest=0x{br:02x} {name} wValue=0x{wv:04x} wIndex=0x{wi:04x} wLen={wl}"
                body_hex = payload[8:]
            else:
                body_hex = payload
            lines.append(tag)
            if body_hex and (a.hex or len(body_hex) <= 8):
                lines.append("    " + " ".join(f"{b:02x}" for b in body_hex))

    if a.sum:
        for k, v in sorted(devs.items()):
            print(f"{k[0]}:{k[1]}  {v} pkts")
        return
    print("\n".join(lines))


if __name__ == "__main__":
    main()
