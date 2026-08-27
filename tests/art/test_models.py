import pytest
from pydantic import ValidationError

from fgo_pet_content.art.models import (
    Anchor,
    ArtAsset,
    ArtManifest,
    Composition,
    Point,
    Rect,
    Size,
    SourceImage,
)


SOURCE = SourceImage(
    path="98001000_merged.png",
    sha256="sha256:source",
    width=1024,
    height=2560,
    mode="RGBA",
)


def _asset(stable_id: str, label: str) -> ArtAsset:
    width, height = (303, 603) if stable_id == "full_body" else (256, 240)
    return ArtAsset(
        stable_id=stable_id,
        semantic_label=label,
        crop_rect=Rect(left=0, top=0, right=width, bottom=height),
        anchor=Anchor(x=width // 2, y=height),
        raw_path=f"raw/{stable_id}.png",
        runtime_path=f"runtime/{stable_id}.png",
    )


def _complete_assets() -> list[ArtAsset]:
    return [_asset("full_body", "常服全身")] + [
        _asset(f"r{row:02d}c{column:02d}", f"表情{row}-{column}")
        for row in range(1, 8)
        for column in range(1, 5)
    ]


def _composition(**updates: object) -> Composition:
    values: dict[str, object] = {
        "body_id": "full_body",
        "default_expression_id": "r01c01",
        "overlay_offset": Point(x=13, y=0),
        "overlay_size": Size(width=256, height=240),
        "panel_anchor": Point(x=151, y=360),
        "default_scale": 0.50,
    }
    values.update(updates)
    return Composition(**values)


def test_manifest_requires_complete_unique_grid() -> None:
    with pytest.raises(ValidationError):
        ArtManifest(
            schema_version=2,
            source=SOURCE,
            assets=[_asset("full_body", "常服全身")],
            composition=_composition(),
        )

    manifest = ArtManifest(
        schema_version=2,
        source=SOURCE,
        assets=_complete_assets(),
        composition=_composition(),
    )
    assert len(manifest.assets) == 29


def test_manifest_rejects_invalid_id_duplicate_label_and_out_of_bounds() -> None:
    assets = _complete_assets()
    assets[1] = assets[1].model_copy(update={"stable_id": "face-1"})
    with pytest.raises(ValidationError):
        ArtManifest(schema_version=2, source=SOURCE, assets=assets, composition=_composition())

    assets = _complete_assets()
    assets[2] = assets[2].model_copy(update={"semantic_label": assets[1].semantic_label})
    with pytest.raises(ValidationError):
        ArtManifest(schema_version=2, source=SOURCE, assets=assets, composition=_composition())

    assets = _complete_assets()
    assets[3] = assets[3].model_copy(
        update={"crop_rect": Rect(left=1000, top=0, right=1100, bottom=100)}
    )
    with pytest.raises(ValidationError):
        ArtManifest(schema_version=2, source=SOURCE, assets=assets, composition=_composition())


def test_manifest_requires_layered_portrait_composition() -> None:
    manifest = ArtManifest(
        schema_version=2,
        source=SOURCE,
        assets=_complete_assets(),
        composition=_composition(),
    )

    assert manifest.composition.body_id == "full_body"
    assert manifest.composition.overlay_offset == Point(x=13, y=0)
    assert manifest.composition.overlay_size == Size(width=256, height=240)
    assert manifest.composition.panel_anchor == Point(x=151, y=360)
    assert manifest.composition.default_scale == 0.50


@pytest.mark.parametrize(
    "composition",
    [
        _composition(body_id="r01c01"),
        _composition(default_expression_id="unknown"),
        _composition(overlay_offset=Point(x=48, y=0)),
        _composition(panel_anchor=Point(x=304, y=360)),
    ],
)
def test_manifest_rejects_invalid_composition(composition: Composition) -> None:
    with pytest.raises(ValidationError):
        ArtManifest(
            schema_version=2,
            source=SOURCE,
            assets=_complete_assets(),
            composition=composition,
        )


@pytest.mark.parametrize("scale", [0, -0.1, 1.01])
def test_composition_rejects_scale_outside_unit_interval(scale: float) -> None:
    with pytest.raises(ValidationError):
        _composition(default_scale=scale)
