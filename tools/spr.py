"""Decoder for Godius Eternal War 'EAST' .spr(.NCS) sprites.

Layout (little endian):
  'EAST' u16 version(0x30) u16 frame_count u16 0
  u32 offsets[frame_count + 1]   (relative to end of table, last = end of data)
Frame:
  u32 0, u16 height(120), 15-byte row bitmap (bit y => row y present)
  then one scanline per set bit:
    u8 n ; n * (u16 skip, u16 len, len palette indices)   (skip+len summed == WIDTH)
Transparency is only expressed by skipped pixels; palette index 0 is an opaque black.
"""
import struct
from PIL import Image

WIDTH = 136
HEIGHT = 120


def read_palette(path):
    b = open(path, 'rb').read()
    return [(b[i + 2], b[i + 1], b[i]) for i in range(0, 1024, 4)]  # BGRX -> RGB


def split_frames(b):
    magic, ver, cnt = struct.unpack('<4sHH', b[:8])
    if magic != b'EAST':
        raise ValueError('not an EAST sprite')
    offs = struct.unpack('<%dI' % (cnt + 1), b[10:10 + 4 * (cnt + 1)])
    base = 10 + 4 * (cnt + 1)
    return [b[base + offs[i]:base + offs[i + 1]] for i in range(cnt)]


def decode_frame(f):
    """Return a HEIGHT x WIDTH list of palette indices (None = not drawn)."""
    h = struct.unpack_from('<H', f, 4)[0]
    mask = int.from_bytes(f[6:21], 'little')
    img = [[None] * WIDTH for _ in range(h)]
    p = 21
    for y in range(h):
        if not (mask >> y) & 1:
            continue
        n = f[p]; p += 1
        x = 0
        for _ in range(n):
            skip, ln = struct.unpack_from('<HH', f, p); p += 4
            x += skip
            img[y][x:x + ln] = f[p:p + ln]; p += ln
            x += ln
    return img


def frame_image(f, pal):
    idx = decode_frame(f)
    im = Image.new('RGBA', (WIDTH, len(idx)), (0, 0, 0, 0))
    px = im.load()
    for y, row in enumerate(idx):
        for x, v in enumerate(row):
            if v is not None:
                px[x, y] = pal[v] + (255,)
    return im
