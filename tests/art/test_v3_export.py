from __future__ import annotations

import json
from dataclasses import replace
from pathlib import Path

import pytest
from PIL import Image, ImageDraw

from fgo_pet_content.art.export import AppearanceExportMetadata, export_appearance_v3
from fgo_pet_content.art.layout_spec import ExpressionRectangle, LayoutProvenance, LayoutSpec
from fgo_pet_content.art.models import Point, Rect, Size
from fgo_pet_content.art.qa import validate_art_bundle


CORE_MAP = {
    "neutral": "r01c01",
    "happy": "r01c01",
    "excited": "r01c02",
    "shy": "r01c02",
    "concerned": "r01c01",
    "sad": "r01c02",
    "surprised": "r01c02",
    "angry": "r01c01",
}


def _source(path: Path) -> bytes:
    image = Image.new("RGBA", (90, 60), (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((20, 4, 70, 26), fill=(100, 130, 190, 255))
    draw.rectangle((5, 32, 40, 56), fill=(200, 80, 100, 255))
    draw.rectangle((50, 32, 85, 56), fill=(70, 150, 100, 255))
    image.save(path, format="PNG")
    return path.read_bytes()


def _layout() -> LayoutSpec:
    return LayoutSpec(
        source_size=Size(width=90, height=60),
        full_body=Rect(left=0, top=0, right=90, bottom=30),
        expressions=(
            ExpressionRectangle(stable_id="r01c01", rect=Rect(left=0, top=30, right=45, bottom=60)),
            ExpressionRectangle(stable_id="r01c02", rect=Rect(left=45, top=30, right=90, bottom=60)),
        ),
        provenance=LayoutProvenance(approval="human_confirmation", confirmed_by="test"),
    )


def _metadata() -> AppearanceExportMetadata:
    return AppearanceExportMetadata(
        appearance_id="casual",
        expression_semantics=CORE_MAP,
        fallback={semantic: "neutral" for semantic in CORE_MAP if semantic != "neutral"},
        overlay_offset=Point(x=0, y=0),
        panel_anchor=Point(x=40, y=25),
        default_scale=0.50,
    )


def test_export_v3_preserves_source_and_writes_relative_runtime_assets(tmp_path: Path) -> None:
    source = tmp_path / "input.png"
    original = _source(source)

    manifest = export_appearance_v3(source, _layout(), _metadata(), tmp_path / "bundle")

    assert source.read_bytes() == original
    assert manifest.schema_version == 3
    assert manifest.appearance_id == "casual"
    assert all(not Path(asset.path).is_absolute() for asset in manifest.assets)
    assert all((tmp_path / "bundle" / asset.path).exists() for asset in manifest.assets)
    assert validate_art_bundle(tmp_path / "bundle").status == "PASS"


def test_export_v3_preserves_preexisting_transparency(tmp_path: Path) -> None:
    source = tmp_path / "input.png"
    _source(source)
    with Image.open(source) as opened:
        image = opened.convert("RGBA")
    image.putpixel((1, 1), (255, 255, 255, 0))
    image.save(source, format="PNG")

    export_appearance_v3(source, _layout(), _metadata(), tmp_path / "bundle")

    with Image.open(tmp_path / "bundle" / "runtime" / "full_body.png") as opened:
        assert opened.convert("RGBA").getpixel((1, 1))[3] == 0


def test_export_v3_uses_explicit_geometry_and_rejects_missing_semantics(tmp_path: Path) -> None:
    source = tmp_path / "input.png"
    _source(source)
    metadata = replace(_metadata(), overlay_offset=Point(x=2, y=3), panel_anchor=Point(x=41, y=24))

    manifest = export_appearance_v3(source, _layout(), metadata, tmp_path / "bundle")

    assert manifest.composition.overlay_offset == Point(x=2, y=3)
    assert manifest.composition.overlay_size == Size(width=45, height=30)
    assert manifest.composition.panel_anchor == Point(x=41, y=24)

    with pytest.raises(ValueError, match="semantic"):
        export_appearance_v3(
            source,
            _layout(),
            replace(metadata, expression_semantics={"neutral": "r01c01"}),
            tmp_path / "missing",
        )


def test_v3_qa_rejects_clipping_and_does_not_echo_source_paths(tmp_path: Path) -> None:
    source = tmp_path / "source-with-private-path.png"
    _source(source)
    clipped = replace(_metadata(), overlay_offset=Point(x=46, y=0))
    bundle = tmp_path / "bundle"
    export_appearance_v3(source, _layout(), clipped, bundle)

    report = validate_art_bundle(bundle)

    assert report.status == "FAIL"
    assert any(error.check_id == "composition.bounds" for error in report.errors)

    manifest = json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))
    manifest["assets"][0]["path"] = str(source)
    (bundle / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    report = validate_art_bundle(bundle)

    assert report.status == "FAIL"
    assert any(error.check_id == "asset.path_safe" for error in report.errors)
    assert str(source) not in (bundle / "qa-report.json").read_text(encoding="utf-8")
