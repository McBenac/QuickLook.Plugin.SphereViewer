#!/usr/bin/env python3
"""Generate the synthetic test images in tests/fixtures/.

The repository never contains real photos. These fixtures are a labelled latitude/longitude test
pattern, so a rendered view shows at a glance whether orientation and projection are right.

    python3 tools/make-fixtures.py      (the video fixture also needs ffmpeg)
"""
import struct
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent.parent / "tests" / "fixtures"
WIDTH, HEIGHT = 2048, 1024

XMP = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:GPano="http://ns.google.com/photos/1.0/panorama/">
<GPano:UsePanoramaViewer>True</GPano:UsePanoramaViewer>
<GPano:ProjectionType>equirectangular</GPano:ProjectionType>
<GPano:FullPanoWidthPixels>{w}</GPano:FullPanoWidthPixels>
<GPano:FullPanoHeightPixels>{h}</GPano:FullPanoHeightPixels>
<GPano:CroppedAreaImageWidthPixels>{w}</GPano:CroppedAreaImageWidthPixels>
<GPano:CroppedAreaImageHeightPixels>{h}</GPano:CroppedAreaImageHeightPixels>
<GPano:CroppedAreaLeftPixels>0</GPano:CroppedAreaLeftPixels>
<GPano:CroppedAreaTopPixels>0</GPano:CroppedAreaTopPixels>
</rdf:Description></rdf:RDF></x:xmpmeta>"""


def font(size):
    for name in ("DejaVuSans-Bold.ttf", "arialbd.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def equirect_pattern():
    """Hue follows longitude, brightness bands follow latitude, grid every 15 degrees."""
    lon = np.linspace(-180, 180, WIDTH, endpoint=False)[None, :].repeat(HEIGHT, 0)
    lat = np.linspace(90, -90, HEIGHT, endpoint=False)[:, None].repeat(WIDTH, 1)
    hue = (lon + 180) / 360
    value = 0.55 + 0.35 * np.cos(np.radians(lat))
    rgb = np.stack([np.clip(np.abs((hue * 6 - k) % 6 - 3) - 1, 0, 1) for k in (0, 4, 2)], -1)
    rgb = rgb * 0.7 * value[..., None] + 0.15
    grid = (np.abs(((lon + 7.5) % 15) - 7.5) < 0.25) | (np.abs(((lat + 7.5) % 15) - 7.5) < 0.25)
    rgb[grid] = 0.05
    rgb[np.abs(lat) < 0.4] = 1.0  # horizon
    image = Image.fromarray((rgb * 255).astype(np.uint8))

    draw = ImageDraw.Draw(image)
    big, small = font(72), font(40)
    for label, longitude in (("N", 0), ("E", 90), ("S", 180), ("W", -90)):
        x = (longitude + 180) / 360 * WIDTH % WIDTH
        draw.text((x, HEIGHT / 2 - 60), label, font=big, fill="white", anchor="ms", stroke_width=4, stroke_fill="black")
    for latitude in (-60, -30, 30, 60):
        y = (90 - latitude) / 180 * HEIGHT
        draw.text((WIDTH / 2 + 20, y - 8), f"{latitude:+d}", font=small, fill="white", anchor="ls", stroke_width=3, stroke_fill="black")
    draw.text((WIDTH / 2, 50), "UP", font=small, fill="white", anchor="mm", stroke_width=3, stroke_fill="black")
    draw.text((WIDTH / 2, HEIGHT - 50), "DOWN", font=small, fill="white", anchor="mm", stroke_width=3, stroke_fill="black")
    return image


# Insta360 X5 lens model, same values as viewer/dual-fisheye.js (rear lens = left half, front = right).
X5 = {
    "rear": dict(yaw=-0.00394, pitch=-0.00047, roll=-0.00868, cx=0.50054, cy=0.49881, k=(0.52771, 0.02174, -0.00108)),
    "front": dict(yaw=-0.00686, pitch=0.00224, roll=0.01392, cx=0.49919, cy=0.50054, k=(0.52575, 0.02321, -0.00172)),
}


def rotation(yaw, pitch, roll, base):
    def ry(a):
        return np.array([[np.cos(a), 0, np.sin(a)], [0, 1, 0], [-np.sin(a), 0, np.cos(a)]])

    def rx(a):
        return np.array([[1, 0, 0], [0, np.cos(a), -np.sin(a)], [0, np.sin(a), np.cos(a)]])

    def rz(a):
        return np.array([[np.cos(a), -np.sin(a), 0], [np.sin(a), np.cos(a), 0], [0, 0, 1]])

    return ry(base) @ ry(yaw) @ rx(pitch) @ rz(roll)


def dual_fisheye(equirect, size=1024):
    """Project the equirectangular pattern into a side-by-side dual-fisheye frame (inverse of the viewer)."""
    src = np.asarray(equirect, dtype=np.float32)
    out = np.zeros((size, 2 * size, 3), dtype=np.float32)
    ys, xs = np.mgrid[0:size, 0:size]
    for half, (name, base) in enumerate((("rear", np.pi), ("front", 0.0))):
        lens = X5[name]
        dx = ((xs + 0.5) / size - lens["cx"]) * 2
        dy = -((ys + 0.5) / size - lens["cy"]) * 2
        r = np.hypot(dx, dy)
        theta = np.linspace(0, np.radians(110), 2000)
        k1, k2, k3 = lens["k"]
        theta = np.interp(r, k1 * theta + k2 * theta ** 3 + k3 * theta ** 5, theta)
        phi = np.arctan2(dy, dx)
        local = np.stack([np.sin(theta) * np.cos(phi), np.sin(theta) * np.sin(phi), np.cos(theta)], -1)
        world = local @ rotation(lens["yaw"], lens["pitch"], lens["roll"], base).T
        lon = np.arctan2(world[..., 0], world[..., 2])
        lat = np.arcsin(np.clip(world[..., 1], -1, 1))
        u = ((lon / (2 * np.pi) + 0.5) * src.shape[1]).astype(int) % src.shape[1]
        v = np.clip(((0.5 - lat / np.pi) * src.shape[0]).astype(int), 0, src.shape[0] - 1)
        frame = src[v, u]
        frame[r > 1.0] = 0  # outside the image circle
        out[:, half * size:(half + 1) * size] = frame
    return Image.fromarray(out.astype(np.uint8))


# Camera "up" written into the synthetic trailer: tilted 30 degrees to the right (viewer frame).
FIXTURE_UP = (0.5, 0.8660, 0.0)


def insta360_trailer(up, seconds=3.0):
    """An Insta360-style trailer: IMU record 0x300 (1 kHz, still camera whose accelerometer reads `up`),
    frame record 0x400 (29.97 fps), a 31-slot directory and the 78-byte footer."""
    x, y, z = up
    imu_axes = (-y, z, x)  # inverse of the viewer mapping (z, -x, y)
    sample = [round(v * 1000) + 0x8000 for v in imu_axes] + [0x8000] * 3  # gyro at rest
    imu = b"".join(struct.pack("<Q6H", 1_000_000 + i * 1000, *sample) for i in range(int(seconds * 1000)))
    frames = b"".join(struct.pack("<Qd", 1_000_000 + round(i * 33367), 0.001) for i in range(int(seconds * 29.97)))
    body, directory = b"", []
    for rec_id, data in ((0x300, imu), (0x400, frames)):
        directory.append(struct.pack("<HII", rec_id >> 8, len(data), len(body)))
        body += data + struct.pack("<HI", rec_id, len(data))
    table = b"".join(directory) + b"\0" * (31 * 10 - 10 * len(directory))
    body += table
    footer = struct.pack("<HI", 0, len(table)) + b"\0" * 32
    trailer_len = len(body) + len(footer) + 4 + 4 + 32
    return body + footer + struct.pack("<II", trailer_len, 3) + b"8db42d694ccc418790edff439fe026bf"


# Photo "up" for the .insp fixture: tilted forward and to the right (viewer frame), so that every axis and
# sign of the mapping is tested.
PHOTO_UP = (0.2506, 0.9023, -0.3509)


def insta360_photo_trailer(up):
    """A photo trailer: no directory, records walked backwards from the footer. IMU record 0x300
    (100 still samples, photo axes) followed by a small 0x200 record, as the camera writes them."""
    x, y, z = up
    imu_axes = (-y, z, x)  # inverse of the viewer mapping (z, -x, y), same as video
    sample = [round(v * 1000) + 0x8000 for v in imu_axes] + [0x8000] * 3
    imu = b"".join(struct.pack("<Q6H", i, *sample) for i in range(100))
    preview = b"\0" * 64
    body = imu + struct.pack("<HI", 0x300, len(imu)) + preview
    footer = struct.pack("<HI", 0x200, len(preview)) + b"\0" * 32
    trailer_len = len(body) + len(footer) + 4 + 4 + 32
    return body + footer + struct.pack("<II", trailer_len, 3) + b"8db42d694ccc418790edff439fe026bf"


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    pattern = equirect_pattern()
    pattern.save(OUT / "equirect-xmp.jpg", quality=85, xmp=XMP.format(w=WIDTH, h=HEIGHT).encode())
    pattern.save(OUT / "plain.jpg", quality=85)
    dual_fisheye(pattern).save(OUT / "dual-fisheye.insp", format="JPEG", quality=85)
    with open(OUT / "dual-fisheye.insp", "ab") as f:
        f.write(insta360_photo_trailer(PHOTO_UP))

    # A 2-second dual-fisheye H.264 clip named like an Insta360 low-res copy, plus an empty .insv
    # with the matching name, so the .insv -> .lrv proxy lookup can be tested.
    stem = "20260101_120000"
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-loop", "1", "-i", str(OUT / "dual-fisheye.insp"),
         "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "2", "-r", "30",
         "-vf", "scale=832:416,hue=h=t*90", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "high",
         "-c:a", "aac", "-shortest", "-movflags", "+faststart", "-f", "mp4", str(OUT / f"LRV_{stem}_01_001.lrv")],
        check=True,
    )
    with open(OUT / f"LRV_{stem}_01_001.lrv", "ab") as f:  # the camera appends its trailer after the MP4
        f.write(insta360_trailer(FIXTURE_UP))
    (OUT / f"VID_{stem}_00_001.insv").write_bytes(b"")
    (OUT / "VID_20260101_130000_00_002.insv").write_bytes(b"")  # no .lrv: must not be handled
    (OUT / "GL010001.lrv").write_bytes(b"")  # another camera's .lrv: must not be handled
    for path in sorted(OUT.iterdir()):
        print(f"{path.name}: {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
