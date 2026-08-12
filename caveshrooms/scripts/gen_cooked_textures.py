"""
Regenerates the cooked-state (partbaked/perfect/charred) textures and the
Temporal Pie filling texture from their source PNGs. Requires Pillow
(`pip install pillow`). Run from anywhere; paths below are relative to the
repo root.

Usage: python scripts/gen_cooked_textures.py
"""
import colorsys
import os
from PIL import Image

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_TEX = os.path.join(REPO_ROOT, "assets", "caveshrooms", "textures", "block", "plant")
PIE_SRC = r"C:\Users\DJRic\AppData\Roaming\Vintagestory\assets\survival\textures\block\food\pie\fill-mushroomblue.png"
PIE_DST = os.path.join(REPO_ROOT, "assets", "caveshrooms", "textures", "block", "food", "pie", "fill-choppedtemporalmushroom.png")

# state -> (hue_target_deg, hue_blend, sat_mult, val_mult)
# Tune these to change how "cooked" each stage looks - hue_target/hue_blend
# control how far it shifts toward brown, sat/val_mult control saturation
# and darkening.
COOK_STATES = {
    "partbaked": (35, 0.25, 1.05, 0.92),
    "perfect":   (32, 0.55, 1.15, 0.80),
    "charred":   (20, 0.85, 0.55, 0.42),
}

# Pie filling recolor target hue (teal, matches the mod's established palette / hue 32 lightHsv)
PIE_TARGET_HUE_DEG = 184


def cook_pixel(r, g, b, a, hue_target_deg, hue_blend, sat_mult, val_mult):
    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    target_h = hue_target_deg / 360.0
    h = h + (target_h - h) * hue_blend
    s = max(0.0, min(1.0, s * sat_mult))
    v = max(0.0, min(1.0, v * val_mult))
    nr, ng, nb = colorsys.hsv_to_rgb(h, s, v)
    return (round(nr * 255), round(ng * 255), round(nb * 255), a)


def make_cooked(src_path, dst_path, params):
    img = Image.open(src_path).convert("RGBA")
    px = img.load()
    w, h = img.size
    out = Image.new("RGBA", (w, h))
    outpx = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            outpx[x, y] = (0, 0, 0, 0) if a == 0 else cook_pixel(r, g, b, a, *params)
    out.save(dst_path)
    print("wrote", dst_path)


def recolor_pie(src_path, dst_path, target_hue_deg):
    img = Image.open(src_path).convert("RGBA")
    px = img.load()
    w, h = img.size
    out = Image.new("RGBA", (w, h))
    outpx = out.load()
    target_h = target_hue_deg / 360.0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a == 0:
                outpx[x, y] = (0, 0, 0, 0)
                continue
            _, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            s = max(0.0, min(1.0, s * 1.1))
            nr, ng, nb = colorsys.hsv_to_rgb(target_h, s, v)
            outpx[x, y] = (round(nr * 255), round(ng * 255), round(nb * 255), a)
    out.save(dst_path)
    print("wrote", dst_path)


if __name__ == "__main__":
    for state, params in COOK_STATES.items():
        make_cooked(os.path.join(BLOCK_TEX, "caveshroom-jim.png"), os.path.join(BLOCK_TEX, f"caveshroom-jim-{state}.png"), params)
        make_cooked(os.path.join(BLOCK_TEX, "caveshroom-jim-gills.png"), os.path.join(BLOCK_TEX, f"caveshroom-jim-gills-{state}.png"), params)

    if os.path.exists(PIE_SRC):
        recolor_pie(PIE_SRC, PIE_DST, PIE_TARGET_HUE_DEG)
    else:
        print(f"skipped pie filling texture - vanilla source not found at {PIE_SRC}")
