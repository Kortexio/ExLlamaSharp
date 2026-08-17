"""Generate ExLlamaSharp.ico (16..256) for Start Menu / executables."""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).with_name("exllamasharp.ico")
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

BG = (15, 23, 42, 255)       # slate-900
RING = (34, 197, 94, 255)    # green-500
CORE = (167, 243, 208, 255)  # green-200
INK = (248, 250, 252, 255)   # slate-50


def _font(px: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for name in ("segoeui.ttf", "SegoeUI.ttf", "arialbd.ttf", "arial.ttf", "calibri.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def render(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = max(1, size // 16)
    d.rounded_rectangle(
        (pad, pad, size - 1 - pad, size - 1 - pad),
        radius=max(3, size // 5),
        fill=BG,
    )
    ring_pad = pad + max(1, size // 10)
    d.ellipse((ring_pad, ring_pad, size - 1 - ring_pad, size - 1 - ring_pad), outline=RING, width=max(1, size // 12))
    core = size // 5
    cx = cy = size // 2
    d.ellipse((cx - core, cy - core, cx + core, cy + core), fill=CORE)
    letter = "E"
    font = _font(max(8, int(size * 0.42)))
    bbox = d.textbbox((0, 0), letter, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text((cx - tw / 2 - bbox[0], cy - th / 2 - bbox[1] - size * 0.02), letter, font=font, fill=INK)
    return img


def main() -> None:
    images = [render(s) for s in SIZES]
    images[-1].save(
        OUT,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=images[:-1],
    )
    images[-1].save(OUT.with_name("exllamasharp-256.png"), format="PNG")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
