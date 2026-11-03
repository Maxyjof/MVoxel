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
BLUE = (71, 169, 232, 255)
BLUE_SIDE = (47, 133, 195, 255)
RED = (243, 119, 119, 255)
RED_SIDE = (214, 91, 99, 255)


def voxels():
    rows = ((0, 1, 4, 5), (0, 1, 2, 3, 4, 5), (0, 1, 2, 3, 4, 5), (0, 1, 4, 5))
    for row, cols in enumerate(rows):
        for col in cols:
            yield col, row, BLUE if row < 2 else RED


def rectangles():
    # Three continuous isometric voxel bands form the selected M silhouette.
    # Each cell is a 3-face cube on a shared 30-degree projection grid.
    for row in range(4):
        cols = range(6) if row else (0, 1, 4, 5)
        for col in cols:
            x, y = 54 + col * 25, 44 + row * 27
            front = BLUE if row < 2 else RED
            side = BLUE_SIDE if row < 2 else RED_SIDE
            top_pts = [(x, y+14), (x+25, y), (x+50, y+14), (x+25, y+28)]
            left_pts = [(x, y+14), (x+25, y+28), (x+25, y+52), (x, y+38)]
            right_pts = [(x+25, y+28), (x+50, y+14), (x+50, y+38), (x+25, y+52)]
            yield (top_pts, front, 0)
            yield (left_pts, side, 0)
            yield (right_pts, front, 0)


def rgba_pixels():
    pixels = bytearray(SIZE * SIZE * 4)
    for points, color, _ in rectangles():
        for y in range(max(0, min(p[1] for p in points)), min(SIZE, max(p[1] for p in points))):
            for x in range(max(0, min(p[0] for p in points)), min(SIZE, max(p[0] for p in points))):
                inside = False
                j = len(points) - 1
                for i, (xi, yi) in enumerate(points):
                    xj, yj = points[j]
                    if ((yi > y) != (yj > y)) and x < (xj-xi)*(y-yi)/(yj-yi+1e-12)+xi:
                        inside = not inside
                    j = i
                if inside:
                    pixels[(y*SIZE+x)*4:(y*SIZE+x+1)*4] = bytes(color)
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
    for points, color, _ in rectangles():
        pts = ' '.join(f'{x},{y}' for x,y in points)
        lines.append(f'  <polygon points="{pts}" fill="#{color[0]:02X}{color[1]:02X}{color[2]:02X}"/>')
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
