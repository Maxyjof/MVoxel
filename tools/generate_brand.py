"""Generate the selected MVoxel brand assets using only the standard library."""
from pathlib import Path
import struct
import zlib

BRAND = Path(__file__).resolve().parents[1] / "MVoxel" / "Editor" / "Brand"
SIZE = 256
# The six faces follow the geometry and palette of the approved 01 design.
FACES = [
    ([(28,73),(78,44),(128,73),(78,102)], "#47a9e8"),
    ([(128,73),(178,44),(228,73),(178,102)], "#47a9e8"),
    ([(78,102),(128,73),(178,102),(128,131)], "#3085c3"),
    ([(28,73),(78,102),(78,160),(28,131)], "#3085c3"),
    ([(178,102),(228,73),(228,131),(178,160)], "#3085c3"),
    ([(28,131),(78,160),(78,210),(28,181)], "#f37777"),
    ([(178,160),(228,131),(228,181),(178,210)], "#f37777"),
    ([(78,102),(128,131),(128,189),(78,160)], "#fa8585"),
    ([(128,131),(178,102),(178,160),(128,189)], "#f37777"),
]

def render(size):
    pixels = bytearray(size * size * 4)
    for points, color in FACES:
        rgb = bytes.fromhex(color[1:]) + b"\xff"
        for y in range(size):
            py = (y + 0.5) * SIZE / size
            for x in range(size):
                px = (x + 0.5) * SIZE / size
                inside = False
                for (x1, y1), (x2, y2) in zip(points, points[1:] + points[:1]):
                    if (y1 > py) != (y2 > py) and px < (x2-x1)*(py-y1)/(y2-y1)+x1:
                        inside = not inside
                if inside:
                    i = (y * size + x) * 4
                    pixels[i:i+4] = rgb
    return bytes(pixels)

def chunk(name, data):
    return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data) & 0xffffffff)

def png(size):
    data = render(size)
    rows = b"".join(b"\0" + data[y*size*4:(y+1)*size*4] for y in range(size))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")

def main():
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">', '<title>MVoxel</title>']
    for points, color in FACES:
        coords = " ".join(f"{x},{y}" for x, y in points)
        svg.append(f'<polygon points="{coords}" fill="{color}"/>')
    svg.append("</svg>")
    BRAND.joinpath("MVoxelLogoGrid.svg").write_text("\n".join(svg) + "\n", encoding="utf-8")
    BRAND.joinpath("MVoxelLogo.png").write_bytes(png(256))
    sizes = (16, 24, 32, 48, 64, 128, 256)
    images = [png(size) for size in sizes]
    offset = 6 + 16 * len(sizes)
    entries = []
    for size, data in zip(sizes, images):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    BRAND.joinpath("MVoxelLogo.ico").write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(images))

if __name__ == "__main__":
    main()
