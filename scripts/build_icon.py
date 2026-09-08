"""Rebuild the Windows icon from the checked-in logo (requires Pillow)."""

from pathlib import Path

from PIL import Image


root = Path(__file__).resolve().parents[1]
source = root / "output" / "imagegen" / "app-logo.png"
destination = source.with_name("app.ico")
sizes = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)]

with Image.open(source) as image:
    image.convert("RGBA").save(destination, format="ICO", sizes=sizes)

with Image.open(destination) as icon:
    if icon.ico.sizes() != set(sizes):
        raise RuntimeError("The ICO is missing expected image sizes")

print("Built " + str(destination.relative_to(root)))
