"""Generate the SSH Dashboard app icon (reproducible artwork).

Run: /opt/homebrew/bin/python3.14 tools/make_icon.py

Produces PNGs, a macOS .icns and a Windows .ico under sshdash/ui/assets/.
The mark is a terminal chevron + cursor bar on a slate squircle, using the
dashboard's accent blue — legible down to 16 px.
"""

from __future__ import annotations

import math
import shutil
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "sshdash" / "ui" / "assets"
OUT.mkdir(parents=True, exist_ok=True)

SIZE = 1024
SCALE = 2  # supersampling
W = SIZE * SCALE

TOP = (34, 47, 68)        # #222F44
BOTTOM = (11, 16, 24)     # #0B1018
ACCENT_TOP = (106, 160, 255)   # #6AA0FF
ACCENT_BOTTOM = (61, 123, 232)  # #3D7BE8
INK = (233, 239, 247)     # #E9EFF7


def squircle_mask(size: int, exponent: float = 5.0) -> Image.Image:
    """Superellipse (Apple-like squircle) mask."""
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    radius = size / 2
    points = []
    steps = 1440
    for index in range(steps):
        angle = 2 * math.pi * index / steps
        cos_a, sin_a = math.cos(angle), math.sin(angle)
        x = radius * math.copysign(abs(cos_a) ** (2 / exponent), cos_a)
        y = radius * math.copysign(abs(sin_a) ** (2 / exponent), sin_a)
        points.append((radius + x, radius + y))
    draw.polygon(points, fill=255)
    return mask


def vertical_gradient(size: int, top: tuple, bottom: tuple) -> Image.Image:
    gradient = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        gradient.putpixel((0, y), color + (255,))
    return gradient.resize((size, size), Image.BILINEAR)


def top_highlight(size: int) -> Image.Image:
    """Soft light falling from the top edge, masked to the squircle later."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    for y in range(int(size * 0.45)):
        alpha = int(16 * (1 - y / (size * 0.45)) ** 2)
        ImageDraw.Draw(layer).line((0, y, size, y), fill=(255, 255, 255, alpha))
    return layer.filter(ImageFilter.GaussianBlur(size / 90))


CENTER = 1024.0


def scaled(point: tuple, factor: float) -> tuple:
    """Scale a glyph coordinate around the canvas centre (optical sizing)."""
    return (
        CENTER + (point[0] - CENTER) * factor,
        CENTER + (point[1] - CENTER) * factor,
    )


def chevron(draw: ImageDraw.ImageDraw, color: tuple, width: int, factor: float = 1.0) -> None:
    points = [scaled(p, factor) for p in ((575, 660), (955, 1024), (575, 1388))]
    width = int(width * factor)
    draw.line(points, fill=color, width=width, joint="curve")
    for x, y in (points[0], points[2]):
        draw.ellipse((x - width / 2, y - width / 2, x + width / 2, y + width / 2), fill=color)


def build_master(glyph_factor: float = 1.0) -> Image.Image:
    # background
    background = vertical_gradient(W, TOP, BOTTOM).convert("RGBA")
    background = Image.alpha_composite(background, top_highlight(W))

    # glyph on its own layer so it can carry a gradient
    glyph_mask = Image.new("L", (W, W), 0)
    glyph_draw = ImageDraw.Draw(glyph_mask)
    chevron(glyph_draw, 255, 132, glyph_factor)
    cursor_top_left = scaled((1065, 1258), glyph_factor)
    cursor_bottom_right = scaled((1485, 1384), glyph_factor)
    glyph_draw.rounded_rectangle(
        (*cursor_top_left, *cursor_bottom_right), radius=63 * glyph_factor, fill=255
    )
    glyph_gradient = vertical_gradient(W, ACCENT_TOP, ACCENT_BOTTOM).convert("RGBA")
    ink_layer = Image.new("RGBA", (W, W), INK + (255,))
    cursor_mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(cursor_mask).rounded_rectangle(
        (*cursor_top_left, *cursor_bottom_right), radius=63 * glyph_factor, fill=255
    )
    chevron_only = Image.new("L", (W, W), 0)
    chevron(ImageDraw.Draw(chevron_only), 255, 132, glyph_factor)

    glyph = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    glyph.paste(ink_layer, (0, 0), cursor_mask)
    accent_layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    accent_layer.paste(glyph_gradient, (0, 0), chevron_only)
    glyph = Image.alpha_composite(accent_layer, glyph)

    # a soft accent shadow under the chevron
    shadow_mask = chevron_only.filter(ImageFilter.GaussianBlur(46))
    shadow = Image.new("RGBA", (W, W), ACCENT_BOTTOM + (90,))
    shadow.putalpha(shadow_mask.point(lambda v: int(v * 0.42)))
    glyph = Image.alpha_composite(shadow, glyph)
    background = Image.alpha_composite(background, glyph)

    # hairline inner border
    from PIL import ImageChops

    inset = 4
    ring = squircle_mask(W)
    inner = Image.new("L", (W, W), 0)
    inner.paste(ring.resize((W - inset * 2, W - inset * 2), Image.LANCZOS), (inset, inset))
    outline = ImageChops.subtract(ring, inner)
    border_layer = Image.new("RGBA", (W, W), (255, 255, 255, 255))
    border_layer.putalpha(outline.point(lambda v: int(v * 0.10)))
    background = Image.alpha_composite(background, border_layer)

    # apply squircle alpha
    master = background.convert("RGBA")
    master.putalpha(squircle_mask(W))
    return master.resize((SIZE, SIZE), Image.LANCZOS)


def main() -> None:
    master = build_master()
    # Small sizes get a slightly larger glyph so the mark survives 16 px.
    small = build_master(1.16)
    master.save(OUT / "icon_1024.png")
    for size in (512, 256, 128, 64):
        master.resize((size, size), Image.LANCZOS).save(OUT / f"icon_{size}.png")
    for size in (32, 16):
        small.resize((size, size), Image.LANCZOS).save(OUT / f"icon_{size}.png")

    # macOS .icns
    iconset = OUT / "SSHDashboard.iconset"
    if iconset.exists():
        shutil.rmtree(iconset)
    iconset.mkdir()
    mapping = {
        16: ["icon_16x16.png"],
        32: ["icon_16x16@2x.png", "icon_32x32.png"],
        64: ["icon_32x32@2x.png"],
        128: ["icon_128x128.png"],
        256: ["icon_128x128@2x.png", "icon_256x256.png"],
        512: ["icon_256x256@2x.png", "icon_512x512.png"],
        1024: ["icon_512x512@2x.png"],
    }
    for size, names in mapping.items():
        source = small if size <= 32 else master
        for name in names:
            source.resize((size, size), Image.LANCZOS).save(iconset / name)
    try:
        subprocess.run(
            ["iconutil", "-c", "icns", str(iconset), "-o", str(OUT / "SSHDashboard.icns")],
            check=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        print(f"iconutil failed ({exc}); .icns not generated")
    shutil.rmtree(iconset, ignore_errors=True)

    # Windows .ico
    master.save(
        OUT / "SSHDashboard.ico",
        sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("wrote:", ", ".join(sorted(path.name for path in OUT.glob("icon_*.png"))))
    print("icns:", (OUT / "SSHDashboard.icns").exists(), "| ico:", (OUT / "SSHDashboard.ico").exists())


if __name__ == "__main__":
    main()
