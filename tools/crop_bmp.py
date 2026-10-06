#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""裁剪 BMP 的一小块并放大，方便看清小字。

用法：python tools/crop_bmp.py <src.bmp> <out.bmp> <x> <y> <w> <h> [scale]
"""
import sys
from pathlib import Path

src, out = Path(sys.argv[1]), Path(sys.argv[2])
x, y, w, h = (int(v) for v in sys.argv[3:7])
scale = int(sys.argv[7]) if len(sys.argv) > 7 else 2

data = src.read_bytes()
off = int.from_bytes(data[10:14], "little")
bw = int.from_bytes(data[18:22], "little", signed=True)
bh = int.from_bytes(data[22:26], "little", signed=True)
bpp = int.from_bytes(data[28:30], "little")
top_down = bh < 0
bh = abs(bh)
assert bpp == 24, f"只支持 24 位 BMP，实际 {bpp}"

row_pad = (4 - (bw * 3) % 4) % 4
stride = bw * 3 + row_pad


def pixel(px, py):
    row = py if top_down else (bh - 1 - py)
    i = off + row * stride + px * 3
    return data[i:i + 3]


ow, oh = w * scale, h * scale
rows = []
for oy in range(oh):
    row = bytearray()
    sy = y + oy // scale
    for ox in range(ow):
        sx = x + ox // scale
        b, g, r = pixel(sx, sy) if (0 <= sx < bw and 0 <= sy < bh) else (0, 0, 0)
        row += bytes((r, g, b))          # BMP 是 BGR，PNG 要 RGB
    rows.append(bytes(row))


def write_png(path: Path, rows, width: int, height: int) -> None:
    """最小 PNG 写出（无第三方依赖）。"""
    import struct
    import zlib as _z

    raw = b"".join(b"\x00" + row for row in rows)     # 每行前置 filter=0
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (len(data).to_bytes(4, "big") + tag + data
                + _z.crc32(tag + data).to_bytes(4, "big"))

    ihdr = (struct.pack(">II", width, height) + bytes((8, 2, 0, 0, 0)))  # 8bit truecolor
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", _z.compress(raw, 6))
        + chunk(b"IEND", b""))


write_png(out, rows, ow, oh)
print(f"OK {out} {ow}x{oh}")
