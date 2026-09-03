import json
from pathlib import Path

import pytest

from fgo_pet_content.art.migrate import migrate_v2_to_v3
from fgo_pet_content.art.models import ArtManifest, Point, Size
from fgo_pet_content.art.v3_models import CORE_EXPRESSION_SEMANTICS


V2_FIXTURE = Path("tests/fixtures/packs/mash-art-v2.json")
SEMANTIC_MAP = {
    "neutral": "r01c04",
    "happy": "r01c01",
    "excited": "r01c02",
    "shy": "r07c01",
    "concerned": "r06c04",
    "sad": "r05c02",
    "surprised": "r04c02",
    "angry": "r02c01",
}


def test_migrate_mash_preserves_stable_ids_and_geometry() -> None:
    v2_manifest = ArtManifest.model_validate_json(
        V2_FIXTURE.read_text(encoding="utf-8")
    )

    result = migrate_v2_to_v3(v2_manifest, SEMANTIC_MAP)

    assert result.schema_version == 3
    assert result.composition.overlay_offset == Point(x=13, y=0)
    assert result.composition.panel_anchor == Point(x=151, y=360)
    assert {asset.stable_id for asset in result.assets} == {
        asset.stable_id for asset in v2_manifest.assets
    }
    assert set(result.expression_semantics) == set(CORE_EXPRESSION_SEMANTICS)
    assert result.expression_semantics == SEMANTIC_MAP


def test_migration_output_is_deterministic() -> None:
    v2_manifest = ArtManifest.model_validate_json(
        V2_FIXTURE.read_text(encoding="utf-8")
    )

    first = migrate_v2_to_v3(v2_manifest, SEMANTIC_MAP)
    second = migrate_v2_to_v3(v2_manifest, dict(reversed(SEMANTIC_MAP.items())))

    assert first.model_dump_json(indent=2) == second.model_dump_json(indent=2)


def test_migration_rejects_overlay_overflow_even_for_untrusted_model_copy() -> None:
    v2_manifest = ArtManifest.model_validate_json(
        V2_FIXTURE.read_text(encoding="utf-8")
    )
    invalid_composition = v2_manifest.composition.model_copy(
        update={"overlay_size": Size(width=400, height=240)}
    )
    untrusted = v2_manifest.model_copy(update={"composition": invalid_composition})

    with pytest.raises(ValueError, match="overlay exceeds body bounds"):
        migrate_v2_to_v3(untrusted, SEMANTIC_MAP)
