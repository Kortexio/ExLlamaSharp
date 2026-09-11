"""Generate docs/assets/social-preview.png (1280x640) for GitHub Open Graph.
Optionally composites a real dashboard screenshot on the right."""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
OUT = HERE / "social-preview.png"
DASH = HERE / "dashboard.png"
W, H = 1280, 640

BG_TOP = (15, 23, 42)
BG_BOT = (30, 41, 59)
TEAL = (13, 148, 136)
TEAL_LIGHT = (45, 212, 191)
INK = (248, 250, 252)
MUTED = (148, 163, 184)
ACCENT_SOFT = (204, 251, 241)


def font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    names = (
        ("segoeuib.ttf", "segoeui.ttf") if bold else ("segoeui.ttf", "arial.ttf")
    )
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def gradient(img: Image.Image) -> None:
    px = img.load()
    for y in range(H):
        t = y / (H - 1)
        r = int(BG_TOP[0] + (BG_BOT[0] - BG_TOP[0]) * t)
        g = int(BG_TOP[1] + (BG_BOT[1] - BG_TOP[1]) * t)
        b = int(BG_TOP[2] + (BG_BOT[2] - BG_TOP[2]) * t)
        for x in range(W):
            px[x, y] = (r, g, b, 255)


def main() -> None:
    img = Image.new("RGBA", (W, H), (0, 0, 0, 255))
    gradient(img)
    d = ImageDraw.Draw(img)

    d.rectangle((0, 0, 12, H), fill=TEAL)

    orb = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    od = ImageDraw.Draw(orb)
    od.ellipse((820, -80, 1380, 480), fill=(*TEAL, 45))
    od.ellipse((980, 280, 1400, 700), fill=(2, 132, 199, 35))
    img = Image.alpha_composite(img, orb)
    d = ImageDraw.Draw(img)

    logo_path = HERE.parents[1] / "packaging" / "assets" / "exllamasharp-256.png"
    mark_box = (64, 56, 64 + 80, 56 + 80)
    d.rounded_rectangle(mark_box, radius=18, fill=(15, 23, 42), outline=TEAL, width=3)
    if logo_path.exists() and logo_path.stat().st_size > 100:
        logo = Image.open(logo_path).convert("RGBA").resize((60, 60), Image.Resampling.LANCZOS)
        img.paste(logo, (74, 66), logo)
        d = ImageDraw.Draw(img)
    else:
        d.text((84, 68), "E", font=font(48, bold=True), fill=INK)

    d.text((164, 68), "ExLlamaSharp", font=font(36, bold=True), fill=INK)
    d.text((166, 112), "by Kortexio", font=font(18), fill=MUTED)

    d.text((64, 180), "Local LLM server", font=font(44, bold=True), fill=INK)
    d.text((64, 236), "for Windows + NVIDIA", font=font(44, bold=True), fill=TEAL_LIGHT)
    d.text(
        (64, 310),
        "OpenAI /v1  ·  Blazor admin  ·  EXL3  ·  No Docker",
        font=font(20),
        fill=MUTED,
    )

    pills = ["Windows Service", "Multi-GPU", "API Keys", "Setup.exe"]
    x, y = 64, 370
    for label in pills:
        f = font(16, bold=True)
        bbox = d.textbbox((0, 0), label, font=f)
        tw = bbox[2] - bbox[0]
        pad_x = 14
        d.rounded_rectangle(
            (x, y, x + tw + pad_x * 2, y + 32),
            radius=16,
            fill=(15, 23, 42),
            outline=TEAL,
            width=2,
        )
        d.text((x + pad_x, y + 6), label, font=f, fill=ACCENT_SOFT)
        x += tw + pad_x * 2 + 10

    d.text((64, 560), "github.com/Kortexio/ExLlamaSharp", font=font(20), fill=TEAL_LIGHT)

    if DASH.exists() and DASH.stat().st_size > 1000:
        shot = Image.open(DASH).convert("RGBA")
        # Crop main content-ish area and fit into right panel
        tw, th = 560, 420
        ratio = max(tw / shot.width, th / shot.height)
        nw, nh = int(shot.width * ratio), int(shot.height * ratio)
        shot = shot.resize((nw, nh), Image.Resampling.LANCZOS)
        left = max(0, (nw - tw) // 2)
        top = max(0, (nh - th) // 2)
        shot = shot.crop((left, top, left + tw, top + th))
        frame = Image.new("RGBA", (tw + 16, th + 16), (0, 0, 0, 0))
        fd = ImageDraw.Draw(frame)
        fd.rounded_rectangle((0, 0, tw + 15, th + 15), radius=16, fill=(15, 23, 42, 220), outline=(*TEAL, 180), width=2)
        frame.paste(shot, (8, 8))
        img.paste(frame, (680, 110), frame)

    img.convert("RGB").save(OUT, format="PNG", optimize=True)
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
