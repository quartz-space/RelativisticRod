#!/usr/bin/env python3
"""
Generates every icon / tile asset for Relativistic Rod Deformation.

    pip install pillow
    python tools/generate_icons.py

Outputs go to ./Assets (PNG tiles for the MSIX manifest + AppIcon.ico for the exe/window).
The glyph is the app's own physics: a swinging rod (blue, true shape) and its
time-delayed image (magenta, apparent shape) pivoting from the same point.
"""
import math
import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets")
os.makedirs(OUT, exist_ok=True)

BG_TOP = (28, 36, 66)
BG_BOT = (10, 13, 28)
TRUE_C = (96, 156, 255)
SEEN_C = (255, 100, 172)
SS = 4  # supersampling


def gradient(w, h):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(BG_TOP[i] * (1 - t) + BG_BOT[i] * t) for i in range(3))
        for x in range(w):
            px[x, y] = c
    return img.convert("RGBA")


def curve_points(n, lag, amp=0.8):
    """Rod pivoted at (0,0) swinging with a wave along its length (unit length)."""
    pts = []
    for i in range(n + 1):
        s = i / n
        ang = amp * math.sin(0.6 - lag - 3.2 * s) * s
        pts.append((s * math.sin(ang), s * math.cos(ang)))
    return pts


def glyph(size):
    """Transparent RGBA glyph (the two rods + pivot) filling a size x size square."""
    big = size * SS
    layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    pivot = (big * 0.612, big * 0.82)
    length = big * 0.66

    def to_px(p):
        return (pivot[0] + p[0] * length, pivot[1] - p[1] * length)

    w_main = max(2, big * 0.062)

    def stroke(points, color, width, alpha=255, target=None):
        d = ImageDraw.Draw(target if target is not None else layer)
        pp = [to_px(p) for p in points]
        col = color + (alpha,)
        r = width / 2
        for i in range(len(pp) - 1):
            (x0, y0), (x1, y1) = pp[i], pp[i + 1]
            steps = max(1, int(math.hypot(x1 - x0, y1 - y0) / (r * 0.35)))
            for k in range(steps + 1):
                t = k / steps
                x, y = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
                d.ellipse([x - r, y - r, x + r, y + r], fill=col)

    true_pts = curve_points(80, 0.0)
    seen_pts = curve_points(80, 0.7)

    # soft glow under the apparent rod
    glow = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    stroke(seen_pts, SEEN_C, w_main * 2.4, alpha=150, target=glow)
    glow = glow.filter(ImageFilter.GaussianBlur(big * 0.022))
    layer = Image.alpha_composite(layer, glow)

    stroke(seen_pts, SEEN_C, w_main * 1.05)
    stroke(true_pts, TRUE_C, w_main)

    # pivot
    d = ImageDraw.Draw(layer)
    pr = big * 0.058
    d.ellipse([pivot[0] - pr, pivot[1] - pr, pivot[0] + pr, pivot[1] + pr], fill=(245, 247, 255, 255))

    # a faint distance ring around an "observer" below the pivot
    return layer.resize((size, size), Image.LANCZOS)


def plated_icon(size):
    """Rounded-square plate with gradient + glyph (used for app icon, tiles, store logo)."""
    big = size * SS
    plate = gradient(big, big)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=int(big * 0.22), fill=255)
    plate.putalpha(mask)

    # subtle soft top sheen
    hl = Image.new("RGBA", (big, big), (255, 255, 255, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-big * 0.3, -big * 0.95, big * 1.3, big * 0.35], fill=(255, 255, 255, 26))
    hl = hl.filter(ImageFilter.GaussianBlur(big * 0.06))
    hl.putalpha(ImageChops.multiply(hl.getchannel("A"), mask))
    plate = Image.alpha_composite(plate, hl)

    out = plate.resize((size, size), Image.LANCZOS)
    g = glyph(size)
    return Image.alpha_composite(out, g)


def wide(w, h):
    """Wide tile / splash: full-bleed gradient with the glyph centred."""
    img = gradient(w, h)
    gs = int(min(w, h) * 0.78)
    g = glyph(gs)
    img.alpha_composite(g, ((w - gs) // 2, (h - gs) // 2))
    return img


def save(img, name):
    path = os.path.join(OUT, name)
    img.save(path)
    print("wrote", os.path.relpath(path))


SCALES = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}

# Square tiles (manifest references the unqualified names; Windows picks the scale)
for base, px in (("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50)):
    for sc, f in SCALES.items():
        if base == "StoreLogo" and sc in (125, 150):
            continue
        save(plated_icon(round(px * f)), f"{base}.scale-{sc}.png")

# Taskbar / title / start-menu list icons
for t in (16, 24, 32, 48, 256):
    img = plated_icon(t)
    save(img, f"Square44x44Logo.targetsize-{t}.png")
    save(img, f"Square44x44Logo.targetsize-{t}_altform-unplated.png")

save(plated_icon(50), "StoreLogo.png")

# Wide tile & splash
for sc in (100, 200, 400):
    f = SCALES[sc]
    save(wide(round(310 * f), round(150 * f)), f"Wide310x150Logo.scale-{sc}.png")
    save(wide(round(620 * f), round(300 * f)), f"SplashScreen.scale-{sc}.png")

# Square 71 / large tile (optional manifest entries)
for sc in (100, 200, 400):
    f = SCALES[sc]
    save(plated_icon(round(71 * f)), f"Square71x71Logo.scale-{sc}.png")
    save(plated_icon(round(310 * f)), f"LargeTile.scale-{sc}.png")

# Multi-size .ico for the exe and window icon
ico_src = plated_icon(256)
ico_path = os.path.join(OUT, "AppIcon.ico")
ico_src.save(ico_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", os.path.relpath(ico_path))

# Big preview for the README / repo social image
save(plated_icon(512), "AppIcon-512.png")
