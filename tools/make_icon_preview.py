import glob, struct, zlib, os, sys

src = sys.argv[1] if len(sys.argv) > 1 else "/tmp/iconprev"
out = sys.argv[2] if len(sys.argv) > 2 else "/tmp/iconprev/preview.png"

files = sorted(glob.glob(os.path.join(src, "*.argb")))
tiles = []
for f in files:
    data = open(f, "rb").read()
    w, h = struct.unpack_from("<ii", data, 0)
    px = list(struct.unpack_from("<%di" % (w * h), data, 8))
    tiles.append((os.path.basename(f), w, h, px))

SCALE = 8
PAD = 6
BG_DARK = (32, 32, 32)
BG_LIGHT = (240, 240, 240)

cell_w = max(t[1] for t in tiles) * SCALE + PAD * 2
cell_h = max(t[2] for t in tiles) * SCALE + PAD * 2
cols = len(tiles)
W = cell_w * cols
H = cell_h * 2

img = [[(0, 0, 0)] * W for _ in range(H)]

def put(x, y, c):
    if 0 <= x < W and 0 <= y < H:
        img[y][x] = c

for row, bg in enumerate((BG_DARK, BG_LIGHT)):
    for i, (name, w, h, px) in enumerate(tiles):
        ox = i * cell_w + PAD
        oy = row * cell_h + PAD
        for y in range(cell_h):
            for x in range(cell_w):
                put(i * cell_w + x, row * cell_h + y, bg)
        for y in range(h):
            for x in range(w):
                p = px[y * w + x]
                a = (p >> 24) & 0xFF
                r = (p >> 16) & 0xFF
                g = (p >> 8) & 0xFF
                b = p & 0xFF
                ar, ag, ab = bg
                cr = (r * a + ar * (255 - a)) // 255
                cg = (g * a + ag * (255 - a)) // 255
                cb = (b * a + ab * (255 - a)) // 255
                for dy in range(SCALE):
                    for dx in range(SCALE):
                        put(ox + x * SCALE + dx, oy + y * SCALE + dy, (cr, cg, cb))

raw = b""
for y in range(H):
    raw += b"\x00" + bytes(v for c in img[y] for v in c)

def chunk(t, d):
    return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(raw, 9))
png += chunk(b"IEND", b"")
open(out, "wb").write(png)
print("order:", [t[0] for t in tiles])
print("png:", W, "x", H, "->", out)
