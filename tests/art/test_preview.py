from __future__ import annotations

from pathlib import Path

from PIL import Image

from fgo_pet_content.art.export import AppearanceExportMetadata, export_appearance_v3
from fgo_pet_content.art.layout_spec import ExpressionRectangle, LayoutProvenance, LayoutSpec
from fgo_pet_content.art.models import Point, Rect, Size
from fgo_pet_content.art.preview import write_preview_artifacts


def _bundle(tmp_path: Path) -> tuple[Path, object]:
    source = tmp_path / "input.png"
    image = Image.new("RGBA", (90, 60), (255, 255, 255, 255))
    image.paste((90, 120, 180, 255), (20, 4, 70, 26))
    image.paste((180, 80, 100, 255), (5, 32, 40, 56))
    image.paste((60, 150, 100, 255), (50, 32, 85, 56))
    image.save(source, format="PNG")
    layout = LayoutSpec(
        source_size=Size(width=90, height=60),
        full_body=Rect(left=0, top=0, right=90, bottom=30),
        expressions=(
            ExpressionRectangle(stable_id="r01c01", rect=Rect(left=0, top=30, right=45, bottom=60)),
            ExpressionRectangle(stable_id="r01c02", rect=Rect(left=45, top=30, right=90, bottom=60)),
        ),
        provenance=LayoutProvenance(approval="explicit_expectation"),
    )
    semantics = {
        "neutral": "r01c01",
        "happy": "r01c01",
        "excited": "r01c02",
        "shy": "r01c02",
        "concerned": "r01c01",
        "sad": "r01c02",
        "surprised": "r01c02",
        "angry": "r01c01",
    }
    metadata = AppearanceExportMetadata(
        appearance_id="casual",
        expression_semantics=semantics,
        fallback={key: "neutral" for key in semantics if key != "neutral"},
        overlay_offset=Point(x=0, y=0),
        panel_anchor=Point(x=40, y=25),
        default_scale=0.50,
    )
    bundle = tmp_path / "bundle"
    return bundle, export_appearance_v3(source, layout, metadata, bundle)


def test_preview_artifacts_are_deterministic_and_cover_all_core_semantics(tmp_path: Path) -> None:
    bundle, manifest = _bundle(tmp_path)

    first = write_preview_artifacts(bundle, manifest, tmp_path / "preview-a")
    second = write_preview_artifacts(bundle, manifest, tmp_path / "preview-b")

    assert first.contact_sheet.read_bytes() == second.contact_sheet.read_bytes()
    assert len(first.composites) == 8
    assert all(path.exists() for path in first.composites)
