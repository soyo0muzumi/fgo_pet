from __future__ import annotations

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from fgo_pet_content.art.v3_models import ArtManifestV3
from fgo_pet_content.packs.models import PackManifestV1


FIXTURES = Path("tests/fixtures/packs")


def test_shared_minimal_manifest_is_accepted_by_python_contract() -> None:
    payload = json.loads((FIXTURES / "valid-minimal" / "package.json").read_text())

    manifest = PackManifestV1.model_validate(payload)

    assert manifest.package_id == "fixture.minimal"
    assert manifest.capabilities == ("art.v3",)


@pytest.mark.parametrize(
    "relative_path",
    [
        "invalid-cases/unknown-capability/package.json",
        "invalid-cases/unsupported-pack-schema/package.json",
        "invalid-cases/traversal/package.json",
    ],
)
def test_shared_invalid_package_manifests_are_rejected(relative_path: str) -> None:
    payload = json.loads((FIXTURES / relative_path).read_text())

    with pytest.raises(ValidationError):
        PackManifestV1.model_validate(payload)


def test_shared_fallback_cycle_is_rejected_by_art_contract() -> None:
    payload = json.loads(
        (
            FIXTURES
            / "invalid-cases"
            / "fallback-cycle"
            / "appearances"
            / "default"
            / "manifest.json"
        ).read_text()
    )

    with pytest.raises(ValidationError):
        ArtManifestV3.model_validate(payload)
