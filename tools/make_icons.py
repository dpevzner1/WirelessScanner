"""Generate every WirelessScanner icon asset from one master design (branding/README.md).

Usage (project root):  python tools/make_icons.py [--master branding/wirelessscanner-icon-master.png]
  * Without --master the built-in design is drawn (amber Wi-Fi arcs over the three spectrum identity colours).
  * With --master, any square PNG (>= 1024 px recommended) becomes the source; every output below is regenerated from it.

Outputs
  branding/wirelessscanner-icon-master.png        1024 px master (only written when drawing the built-in design)
  branding/png/icon-<N>.png                       16 ... 1024 px
  branding/windows/WirelessScanner.ico            16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px (PNG-compressed entries)
  branding/android/mipmap-*/ic_launcher*.png      legacy, round and adaptive-foreground launcher icons (48-192 dp sets)
  branding/android/playstore-icon.png             512 px store listing icon
  branding/android/values/ic_launcher_background.xml
Then copies the .ico to WirelessScanner.Presentation/Assets/WirelessScanner.ico and WirelessScanner.Setup/icon.ico.
Small sizes (<= 32 px) use a simplified drawing (arcs only) so they stay legible.
"""
from __future__ import annotations

import argparse
import shutil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "branding"
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
PNG_SIZES = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 144, 192, 256, 512, 1024]
ANDROID = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
BG_HEX = "#0B0705"

AMBER_HI, AMBER, AMBER_LO = (255, 176, 0), (255, 136, 0), (255, 102, 0)
IDENTITY = [(57, 135, 229), (217, 89, 38), (25, 158, 112)]  # validated spectrum identity colours (DR-0010)


def draw(size: int = 1024, simple: bool = False, full_bleed: bool = False) -> Image.Image:
    """Draw at 4x and downsample for clean anti-aliasing."""
    S = size * 4
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 0 if full_bleed else int(S * 0.04)
    radius = int(S * 0.22)
    # background tile: obsidian with a warm vertical glow
    tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    td = ImageDraw.Draw(tile)
    for y in range(S):
        t = y / S
        c = (int(18 * (1 - t) + 6 * t), int(10 * (1 - t) + 4 * t), int(4 * (1 - t) + 3 * t), 255)
        td.line([(0, y), (S, y)], fill=c)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=0 if full_bleed else radius, fill=255)
    img.paste(tile, (0, 0), mask)
    if not full_bleed:
        d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, outline=(255, 102, 0, 150), width=max(4, S // 90))

    cx, cy = S // 2, int(S * (0.70 if not simple else 0.80))
    if not simple:
        # spectrum: three flat-topped channel masks along the floor, identity colours
        base = int(S * 0.80)
        for (x0, x1, top), col in zip([(0.17, 0.43, 0.60), (0.36, 0.64, 0.66), (0.57, 0.83, 0.62)], IDENTITY):
            l, r, tp = int(S * x0), int(S * x1), int(S * top)
            ramp = int((r - l) * 0.14)
            poly = [(l, base), (l + ramp, tp), (r - ramp, tp), (r, base)]
            layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
            ld = ImageDraw.Draw(layer)
            ld.polygon(poly, fill=col + (70,))
            ld.line(poly, fill=col + (235,), width=max(6, S // 110), joint="curve")
            img = Image.alpha_composite(img, layer)
        d = ImageDraw.Draw(img)
        d.line([(int(S * 0.13), base), (int(S * 0.87), base)], fill=(255, 136, 0, 120), width=max(4, S // 160))

    # Wi-Fi arcs with a soft glow
    arcs = [0.16, 0.29, 0.42] if not simple else [0.27, 0.50]
    width = int(S * (0.065 if not simple else 0.13))
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    sharp = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sharp)
    for i, r in enumerate(arcs):
        R = int(S * r)
        box = [cx - R, cy - R, cx + R, cy + R]
        col = [AMBER_LO, AMBER, AMBER_HI][min(i, 2)] if not simple else [AMBER, AMBER_HI][i]
        gd.arc(box, start=225, end=315, fill=col + (160,), width=width + S // 40)
        sd.arc(box, start=225, end=315, fill=col + (255,), width=width)
    dot = int(S * (0.055 if not simple else 0.10))
    gd.ellipse([cx - dot - S // 80, cy - dot - S // 80, cx + dot + S // 80, cy + dot + S // 80], fill=AMBER + (160,))
    sd.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=AMBER_HI + (255,))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(S // 60)))
    img = Image.alpha_composite(img, sharp)
    # keep everything inside the tile
    if not full_bleed:
        clip = Image.new("L", (S, S), 0)
        ImageDraw.Draw(clip).rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, fill=255)
        img.putalpha(Image.composite(img.getchannel("A"), Image.new("L", (S, S), 0), clip))
    return img.resize((size, size), Image.LANCZOS)


def from_master(master: Image.Image, size: int) -> Image.Image:
    return master.resize((size, size), Image.LANCZOS)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--master")
    a = ap.parse_args()
    (OUT / "png").mkdir(parents=True, exist_ok=True)
    (OUT / "windows").mkdir(parents=True, exist_ok=True)

    if a.master:
        master = Image.open(ROOT / a.master).convert("RGBA")
        render = lambda n, simple=False: from_master(master, n)
    else:
        master = draw(1024)
        master.save(OUT / "wirelessscanner-icon-master.png")
        render = lambda n, simple=False: draw(n, simple=simple)

    for n in PNG_SIZES:
        render(n, n <= 32).save(OUT / "png" / f"icon-{n}.png")

    ico_imgs = [render(n, n <= 32) for n in ICO_SIZES]
    ico = OUT / "windows" / "WirelessScanner.ico"
    ico_imgs[-1].save(ico, format="ICO", sizes=[(n, n) for n in ICO_SIZES], append_images=ico_imgs[:-1])

    for dpi, n in ANDROID.items():
        folder = OUT / "android" / f"mipmap-{dpi}"
        folder.mkdir(parents=True, exist_ok=True)
        legacy = render(n, n <= 48)
        legacy.save(folder / "ic_launcher.png")
        circle = Image.new("L", (n, n), 0)
        ImageDraw.Draw(circle).ellipse([0, 0, n - 1, n - 1], fill=255)
        rnd = render(n, n <= 48).copy()
        rnd.putalpha(Image.composite(rnd.getchannel("A"), Image.new("L", (n, n), 0), circle))
        rnd.save(folder / "ic_launcher_round.png")
        # adaptive foreground: 108 dp canvas, artwork inside the 72 dp safe zone (66.7 %)
        fg_size = int(n * 108 / 48)
        art = draw(int(fg_size * 0.667), simple=False, full_bleed=True) if not a.master else from_master(master, int(fg_size * 0.667))
        fg = Image.new("RGBA", (fg_size, fg_size), (0, 0, 0, 0))
        off = (fg_size - art.width) // 2
        fg.paste(art, (off, off), art)
        fg.save(folder / "ic_launcher_foreground.png")
    render(512).save(OUT / "android" / "playstore-icon.png")
    vals = OUT / "android" / "values"
    vals.mkdir(parents=True, exist_ok=True)
    (vals / "ic_launcher_background.xml").write_text(
        f'<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">{BG_HEX}</color>\n</resources>\n', encoding="utf-8")

    for dest in [ROOT / "WirelessScanner.Presentation" / "Assets" / "WirelessScanner.ico", ROOT / "WirelessScanner.Setup" / "icon.ico"]:
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ico, dest)
    print("icons written to", OUT)


if __name__ == "__main__":
    main()
