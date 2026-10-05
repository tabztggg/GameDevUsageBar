"""Generate the original GameDevUsageBar monogram using only the Python standard library."""
import math
import pathlib
import struct
import zlib

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)

def color(x, y):
    # Rounded blue tile with a geometric white G.
    qx, qy = max(abs(x-.5)-.32, 0), max(abs(y-.5)-.32, 0)
    if math.hypot(qx, qy) > .16:
        return (0, 0, 0, 0)
    dx, dy = x-.5, y-.5
    radius, angle = math.hypot(dx, dy), math.atan2(dy, dx)
    ring = .19 <= radius <= .30 and not (-.65 < angle < .15)
    bar = .49 <= x <= .80 and .49 <= y <= .56
    hook = .73 <= x <= .80 and .51 <= y <= .66
    return (244, 248, 255, 255) if ring or bar or hook else (78, 101, 201, 255)

def png(size):
    rows = bytearray()
    for py in range(size):
        rows.append(0)
        for px in range(size):
            samples = [color((px+(sx+.5)/4)/size, (py+(sy+.5)/4)/size) for sy in range(4) for sx in range(4)]
            alpha = sum(c[3] for c in samples)
            rows.extend([round(sum(c[i]*c[3] for c in samples)/alpha) if alpha else 0 for i in range(3)] + [round(alpha/16)])
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')

sizes = [16, 32, 48, 64, 256]
frames = [png(size) for size in sizes]
offset = 6 + 16 * len(sizes)
entries = bytearray()
for size, frame in zip(sizes, frames):
    entries.extend(struct.pack('<BBBBHHII', size if size < 256 else 0, size if size < 256 else 0, 0, 0, 1, 32, len(frame), offset))
    offset += len(frame)
path = pathlib.Path(__file__).resolve().parents[1] / 'src/GameDevUsageBar.App/Assets/GameDevUsageBar.ico'
path.parent.mkdir(parents=True, exist_ok=True)
path.write_bytes(struct.pack('<HHH', 0, 1, len(sizes)) + entries + b''.join(frames))
print(path)
