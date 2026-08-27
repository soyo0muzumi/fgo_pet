import pytest
from pydantic import ValidationError

from fgo_pet_content.art.models import Anchor, ArtAsset, ArtManifest, Rect, SourceImage


SOURCE = SourceImage(
    path="98001000_merged.png",
    sha256="sha256:source",
    width=1024,
    height=2560,
    mode="RGBA",
)


def _asset(stable_id: str, label: str) -> ArtAsset:
    return ArtAsset(
        stable_id=stable_id,
        semantic_label=label,
        crop_rect=Rect(left=0, top=0, right=100, bottom=100),
        anchor=Anchor(x=50, y=100),
        raw_path=f"raw/{stable_id}.png",
        runtime_path=f"runtime/{stable_id}.png",
    )


def _complete_assets() -> list[ArtAsset]:
    return [_asset("full_body", "常服全身")] + [
        _asset(f"r{row:02d}c{column:02d}", f"表情{row}-{column}")
        for row in range(1, 8)
        for column in range(1, 5)
    ]


def test_manifest_requires_complete_unique_grid() -> None:
    with pytest.raises(ValidationError):
        ArtManifest(source=SOURCE, assets=[_asset("full_body", "常服全身")])

    manifest = ArtManifest(source=SOURCE, assets=_complete_assets())
    assert len(manifest.assets) == 29


def test_manifest_rejects_invalid_id_duplicate_label_and_out_of_bounds() -> None:
    assets = _complete_assets()
    assets[1] = assets[1].model_copy(update={"stable_id": "face-1"})
    with pytest.raises(ValidationError):
        ArtManifest(source=SOURCE, assets=assets)

    assets = _complete_assets()
    assets[2] = assets[2].model_copy(update={"semantic_label": assets[1].semantic_label})
    with pytest.raises(ValidationError):
        ArtManifest(source=SOURCE, assets=assets)

    assets = _complete_assets()
    assets[3] = assets[3].model_copy(
        update={"crop_rect": Rect(left=1000, top=0, right=1100, bottom=100)}
    )
    with pytest.raises(ValidationError):
        ArtManifest(source=SOURCE, assets=assets)
