import hashlib
from pathlib import Path

from PIL import Image, ImageDraw

from fgo_pet_content.art.background import remove_edge_background
from fgo_pet_content.art.export import export_art_bundle


def test_existing_alpha_is_preserved_exactly() -> None:
    image = Image.new("RGBA", (12, 12), (38, 36, 44, 0))
    draw = ImageDraw.Draw(image)
    draw.rectangle((2, 2, 9, 11), fill=(24, 24, 30, 255))
    draw.ellipse((3, 1, 8, 6), fill=(220, 170, 190, 255))
    image.putpixel((3, 1), (220, 170, 190, 96))
    image.putpixel((0, 0), (38, 36, 44, 0))

    cleaned = remove_edge_background(image, tolerance=32, feather=2)

    assert cleaned.tobytes() == image.tobytes()


def test_only_edge_connected_dark_pixels_are_removed() -> None:
    image = Image.new("RGBA", (12, 12), (30, 30, 35, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((2, 2, 9, 9), fill=(220, 170, 190, 255))
    draw.rectangle((5, 5, 6, 6), fill=(32, 32, 36, 255))

    cleaned = remove_edge_background(image, tolerance=12, feather=0)

    assert cleaned.getpixel((0, 0))[3] == 0
    assert cleaned.getpixel((5, 5))[3] == 255


def _sheet(path: Path) -> None:
    image = Image.new("RGBA", (1024, 2443), (38, 36, 44, 0))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 302, 602), fill=(40, 40, 45, 255))
    draw.ellipse((70, 10, 230, 190), fill=(220, 170, 190, 255))
    for row in range(7):
        top = 623 + row * 260
        for column in range(4):
            left = column * 256
            draw.rectangle((left + 20, top, left + 235, top + 239), fill=(40, 40, 45, 255))
            draw.ellipse((left + 48, top + 4, left + 208, top + 180), fill=(220, 170, 190, 255))
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
    assert manifest.schema_version == 2
    assert manifest.composition.body_id == "full_body"
    assert manifest.composition.default_expression_id == "r01c01"
    assert manifest.composition.overlay_offset.x == 24
    assert manifest.composition.overlay_offset.y == 0
    assert manifest.composition.overlay_size.width == 256
    assert manifest.composition.overlay_size.height == 240
    assert manifest.composition.panel_anchor.x == 151
    assert manifest.composition.panel_anchor.y == 360
    assert manifest.composition.default_scale == 0.60
