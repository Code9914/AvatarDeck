"""Generates the AvatarDeck icon (PNG + multi-size ICO) without third-party packages.

A rounded glass tile with a violet-to-cyan gradient and a 2x2 grid of controls (one "on" toggle).
Run from the repository root:  python tools/make_icon.py
"""
import struct
import zlib

TOP = (124, 92, 255)      # violet
BOTTOM = (45, 196, 255)   # cyan


def coverage_rounded_rect(x, y, x0, y0, x1, y1, r):
    """1 inside the rounded rectangle, 0 outside (point sample)."""
    if x < x0 or x > x1 or y < y0 or y > y1:
        return 0.0
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return 1.0 if (x - cx) ** 2 + (y - cy) ** 2 <= r * r else 0.0


def render(size, ss=4):
    """Returns RGBA bytes (straight alpha), supersampled ss x ss."""
    out = bytearray(size * size * 4)
    s = 1.0 / size
    tiles = [
        (0.24, 0.24, 0.47, 0.47, True),
        (0.53, 0.24, 0.76, 0.47, False),
        (0.24, 0.53, 0.47, 0.76, False),
        (0.53, 0.53, 0.76, 0.76, False),
    ]
    for py in range(size):
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    x = (px + (sx + 0.5) / ss) * s
                    y = (py + (sy + 0.5) / ss) * s
                    a = coverage_rounded_rect(x, y, 0.04, 0.04, 0.96, 0.96, 0.22)
                    if a == 0:
                        continue
                    t = (x + y) / 2
                    col = [TOP[i] + (BOTTOM[i] - TOP[i]) * t for i in range(3)]
                    # soft highlight at the top (glass)
                    if y < 0.45:
                        k = (0.45 - y) / 0.45 * 0.18
                        col = [c + (255 - c) * k for c in col]
                    for x0, y0, x1, y1, on in tiles:
                        if coverage_rounded_rect(x, y, x0, y0, x1, y1, 0.06):
                            if on:
                                col = [255, 255, 255]
                            else:
                                col = [c + (255 - c) * 0.35 for c in col]
                                col = [c * 0.82 for c in col]
                            break
                    for i in range(3):
                        acc[i] += col[i] * a
                    acc[3] += a
            n = ss * ss
            alpha = acc[3] / n
            o = (py * size + px) * 4
            if alpha > 0:
                for i in range(3):
                    out[o + i] = int(round(acc[i] / acc[3]))
                out[o + 3] = int(round(alpha * 255))
    return bytes(out)


def png(size, rgba):
    raw = b"".join(b"\x00" + rgba[y * size * 4:(y + 1) * size * 4] for y in range(size))

    def chunk(kind, data):
        c = struct.pack(">I", len(data)) + kind + data
        return c + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


if __name__ == "__main__":
    pngs = {size: png(size, render(size)) for size in (16, 24, 32, 48, 64, 128, 256)}
    with open("src/AvatarDeck.UI/Assets/avatardeck.ico", "wb") as f:
        f.write(ico(sorted(pngs.items())))
    with open("src/AvatarDeck.App/avatardeck.png", "wb") as f:
        f.write(pngs[256])
    print("ok")
