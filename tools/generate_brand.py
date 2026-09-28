"""Generate the MVoxel mark, PNG and multi-size Windows icon without dependencies.

Run from any directory: python tools/generate_brand.py
All geometry is made of square voxels; the transparent canvas is never a badge.
"""

from pathlib import Path
import struct
import zlib


ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "MVoxel" / "Editor" / "Brand"
SIZE = 256
CELL = 18
ORIGIN_X = 20
ORIGIN_Y = 20

BLUE = (29, 103, 196, 255)
BLUE_FACE = (53, 165, 239, 255)
BLUE_GLEAM = (130, 221, 255, 255)
BLUE_SHADE = (13, 59, 132, 255)
RED = (187, 45, 65, 255)
RED_FACE = (245, 80, 91, 255)
RED_GLEAM = (255, 160, 143, 255)
RED_SHADE = (121, 24, 52, 255)


def voxels():
    for row in range(12):
        blue = {0, 1}
        red = {10, 11}
        if row <= 3:
            blue.update((row + 1, row + 2))
            red.update((9 - row, 10 - row))
        elif row == 4:
            blue.update((4, 5))
            red.update((6, 7))
        for col in sorted(blue):
            yield col, row, (BLUE, BLUE_FACE, BLUE_GLEAM, BLUE_SHADE)
        for col in sorted(red):
            yield col, row, (RED, RED_FACE, RED_GLEAM, RED_SHADE)


def rectangles():
    for col, row, colors in voxels():
        x = ORIGIN_X + col * CELL
        y = ORIGIN_Y + row * CELL
        # Square body, inset square face, and two square light/shadow pixels.
        yield x, y, 17, colors[0]
        yield x + 2, y + 2, 13, colors[1]
        yield x + 3, y + 3, 4, colors[2]
        yield x + 11, y + 11, 4, colors[3]


def rgba_pixels():
    pixels = bytearray(SIZE * SIZE * 4)
    for x, y, side, color in rectangles():
        for py in range(y, y + side):
            offset = (py * SIZE + x) * 4
            pixels[offset : offset + side * 4] = bytes(color) * side
    return bytes(pixels)


def png_chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


def png(data, size):
    scanlines = b"".join(b"\0" + data[y * size * 4 : (y + 1) * size * 4] for y in range(size))
    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(scanlines, 9))
        + png_chunk(b"IEND", b"")
    )


def resize_nearest(data, size):
    out = bytearray(size * size * 4)
    for y in range(size):
        sy = min(SIZE - 1, (y * SIZE + SIZE // 2) // size)
        for x in range(size):
            sx = min(SIZE - 1, (x * SIZE + SIZE // 2) // size)
            out[(y * size + x) * 4 : (y * size + x + 1) * 4] = data[(sy * SIZE + sx) * 4 : (sy * SIZE + sx + 1) * 4]
    return bytes(out)


def svg():
    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" viewBox="0 0 {SIZE} {SIZE}" shape-rendering="crispEdges">',
        '  <title>MVoxel</title>',
        '  <desc>Red and blue M composed exclusively of equal-grid square voxels on transparency.</desc>',
    ]
    for x, y, side, color in rectangles():
        lines.append(f'  <rect x="{x}" y="{y}" width="{side}" height="{side}" fill="#{color[0]:02X}{color[1]:02X}{color[2]:02X}"/>')
    lines.append('</svg>')
    return "\n".join(lines) + "\n"


def ico(source):
    sizes = (16, 24, 32, 48, 64, 128, 256)
    entries = []
    images = []
    offset = 6 + 16 * len(sizes)
    for size in sizes:
        image = png(source if size == SIZE else resize_nearest(source, size), size)
        entries.append(struct.pack("<BBBBHHII", size if size < 256 else 0, size if size < 256 else 0, 0, 0, 1, 32, len(image), offset))
        images.append(image)
        offset += len(image)
    return struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(images)


def main():
    source = rgba_pixels()
    BRAND.joinpath("MVoxelLogoGrid.svg").write_text(svg(), encoding="utf-8")
    BRAND.joinpath("MVoxelLogo.png").write_bytes(png(source, SIZE))
    BRAND.joinpath("MVoxelLogo.ico").write_bytes(ico(source))


if __name__ == "__main__":
    main()
