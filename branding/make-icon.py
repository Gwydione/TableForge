"""Builds TableForge/Assets/TableForge.ico from the canonical artwork, branding/TableForge-icon-source.png.

The artwork itself is never edited: this only scales it down (Lanczos) to the standard Windows icon sizes, keeping its
transparency, and packs those into one .ico (Windows icons hold at most 256 x 256 per image). Run it again only if the
source artwork changes:

    python branding/make-icon.py        (needs Pillow: pip install pillow)
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "branding" / "TableForge-icon-source.png"
TARGET = ROOT / "TableForge" / "Assets" / "TableForge.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

art = Image.open(SOURCE)
assert art.mode == "RGBA" and art.width == art.height, "the source must be a square RGBA PNG"
art.save(TARGET, format="ICO", sizes=[(s, s) for s in SIZES])
print(f"wrote {TARGET} ({', '.join(str(s) for s in SIZES)} px)")
