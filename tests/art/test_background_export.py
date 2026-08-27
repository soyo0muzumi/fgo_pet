import hashlib
from pathlib import Path

from PIL import Image, ImageDraw

from fgo_pet_content.art.background import remove_edge_background
from fgo_pet_content.art.export import export_art_bundle


def test_only_edge_connected_dark_pixels_are_removed() -> None:
    image = Image.new("RGBA", (12, 12), (30, 30, 35, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((2, 2, 9, 9), fill=(220, 170, 190, 255))
    draw.rectangle((5, 5, 6, 6), fill=(32, 32, 36, 255))

    cleaned = remove_edge_background(image, tolerance=12, feather=0)

    assert cleaned.getpixel((0, 0))[3] == 0
    assert cleaned.getpixel((5, 5))[3] == 255


def _sheet(path: Path) -> None:
    image = Image.new("RGBA", (120, 174), (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((45, 2, 74, 17), fill=(40, 40, 45, 255))
    draw.rectangle((52, 4, 67, 16), fill=(220, 170, 190, 255))
    for row in range(7):
        top = 22 + row * 22
        draw.rectangle((0, top, 119, top + 17), fill=(40, 40, 45, 255))
        for column in range(4):
            left = column * 30 + 8
            draw.rectangle((left, top + 2, left + 12, top + 15), fill=(220, 170, 190, 255))
    image.save(path)


def test_export_preserves_source_and_writes_raw_and_runtime_assets(
    tmp_path: Path,
) -> None:
    source = tmp_path / "sheet.png"
    _sheet(source)
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    labels = {
        f"r{row:02d}c{column:02d}": f"表情{row}-{column}"
        for row in range(1, 8)
        for column in range(1, 5)
    }

    manifest = export_art_bundle(source, tmp_path / "bundle", labels)

    assert hashlib.sha256(source.read_bytes()).hexdigest() == before
    assert len(list((tmp_path / "bundle" / "raw").rglob("*.png"))) == 29
    assert len(list((tmp_path / "bundle" / "runtime").rglob("*.png"))) == 29
    assert (tmp_path / "bundle" / "manifest.json").exists()
    assert len(manifest.assets) == 29
    assert all(asset.raw_sha256 for asset in manifest.assets)
    assert all(asset.runtime_sha256 for asset in manifest.assets)
