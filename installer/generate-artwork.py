#!/usr/bin/env python3
"""Generate the Inno Setup artwork and Windows icon from the app icon."""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
ASSETS = ROOT / "assets"
ICON = ROOT.parent / "dotnet" / "assets" / "appicon.png"
ASSETS.mkdir(parents=True, exist_ok=True)

NAVY = (10, 15, 22)
NAVY_2 = (20, 30, 44)
BLUE = (76, 141, 255)
MUTED = (147, 164, 184)
FAINT = (90, 108, 128)
WHITE = (235, 242, 250)
FONT_BOLD = "/System/Library/Fonts/Supplemental/Arial Bold.ttf"
FONT_REG = "/System/Library/Fonts/Supplemental/Arial.ttf"


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(FONT_BOLD if bold else FONT_REG, size)


def rounded(draw, box, radius, fill=None, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def vertical_gradient(size, top, bottom):
    image = Image.new("RGB", size, top)
    draw = ImageDraw.Draw(image)
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        color = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
        draw.line((0, y, size[0], y), fill=color)
    return image


def large_wizard() -> Image.Image:
    scale = 4
    w, h = 164 * scale, 314 * scale
    image = vertical_gradient((w, h), NAVY, NAVY_2)
    draw = ImageDraw.Draw(image, "RGBA")

    # Fine blueprint grid and a soft blue glow.
    for x in range(0, w, 14 * scale):
        draw.line((x, 0, x, h), fill=(255, 255, 255, 7), width=1)
    for y in range(0, h, 14 * scale):
        draw.line((0, y, w, y), fill=(255, 255, 255, 7), width=1)
    for radius in range(90 * scale, 0, -2 * scale):
        alpha = int(10 * (1 - radius / (90 * scale)))
        draw.ellipse((w // 2 - radius, 110 * scale - radius,
                      w // 2 + radius, 110 * scale + radius),
                     fill=(76, 141, 255, alpha))

    icon = Image.open(ICON).convert("RGBA").resize((58 * scale, 58 * scale), Image.Resampling.LANCZOS)
    image.paste(icon, (53 * scale, 26 * scale), icon)

    def centered(text, y, f, fill):
        box = draw.textbbox((0, 0), text, font=f)
        draw.text(((w - (box[2] - box[0])) / 2, y), text, font=f, fill=fill)

    centered("SSH", 94 * scale, font(30 * scale, True), WHITE)
    centered("D A S H B O A R D", 126 * scale, font(9 * scale, True), BLUE)

    draw.line((22 * scale, 148 * scale, 142 * scale, 148 * scale),
              fill=(76, 141, 255, 100), width=1 * scale)

    # Terminal card.
    rounded(draw, (18 * scale, 166 * scale, 146 * scale, 252 * scale),
            8 * scale, fill=(9, 14, 20, 235), outline=(45, 62, 80, 255), width=1 * scale)
    for i, color in enumerate(((255, 107, 107), (255, 196, 87), (63, 207, 142))):
        draw.ellipse(((27 + i * 9) * scale, 174 * scale, (31 + i * 9) * scale, 178 * scale),
                     fill=color + (220,))
    draw.line((26 * scale, 184 * scale, 138 * scale, 184 * scale),
              fill=(45, 62, 80, 220), width=1 * scale)

    prompt_font = font(25 * scale, True)
    draw.text((32 * scale, 197 * scale), ">", font=prompt_font, fill=BLUE)
    draw.rounded_rectangle((52 * scale, 215 * scale, 90 * scale, 220 * scale),
                           radius=2 * scale, fill=WHITE)
    draw.text((112 * scale, 197 * scale), "ssh", font=font(9 * scale, True), fill=MUTED)

    centered("SECURE SHELL WORKSPACE", 272 * scale, font(7 * scale, True), FAINT)
    centered("JPHsystems", 288 * scale, font(8 * scale, True), MUTED)
    draw.line((64 * scale, 301 * scale, 100 * scale, 301 * scale),
              fill=(76, 141, 255, 160), width=1 * scale)
    return image.resize((164, 314), Image.Resampling.LANCZOS)


def small_wizard() -> Image.Image:
    scale = 4
    w, h = 55 * scale, 58 * scale
    image = Image.new("RGB", (w, h), NAVY)
    draw = ImageDraw.Draw(image, "RGBA")
    icon = Image.open(ICON).convert("RGBA").resize((42 * scale, 42 * scale), Image.Resampling.LANCZOS)
    image.paste(icon, (6 * scale, 7 * scale), icon)
    draw.rectangle((0, h - 2 * scale, w, h), fill=BLUE)
    return image.resize((55, 58), Image.Resampling.LANCZOS)


large_wizard().save(ASSETS / "wizard-large.bmp", "BMP")
small_wizard().save(ASSETS / "wizard-small.bmp", "BMP")

icon = Image.open(ICON).convert("RGBA")
icon.save(ASSETS / "setup.ico",
          sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", *(p.name for p in sorted(ASSETS.iterdir())))
