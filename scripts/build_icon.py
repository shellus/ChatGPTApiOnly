"""Rebuild desktop icons from the checked-in logo (requires Pillow)."""

from pathlib import Path

from PIL import Image


root = Path(__file__).resolve().parents[1]
source = root / "output" / "imagegen" / "app-logo.png"
destination = source.with_name("app.ico")
sizes = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)]

with Image.open(source) as image:
    image = image.convert("RGBA")
    image.save(destination, format="ICO", sizes=sizes)
    tauri_icons = root / "src-tauri" / "icons"
    tauri_icons.mkdir(parents=True, exist_ok=True)
    image.save(tauri_icons / "icon.ico", format="ICO", sizes=sizes)
    image.resize((256, 256)).save(tauri_icons / "icon.png")
    image.save(tauri_icons / "icon.icns", format="ICNS")

with Image.open(destination) as icon:
    if icon.ico.sizes() != set(sizes):
        raise RuntimeError("The ICO is missing expected image sizes")

print("Built " + str(destination.relative_to(root)))
