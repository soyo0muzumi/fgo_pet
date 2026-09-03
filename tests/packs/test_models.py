from __future__ import annotations

import json

import pytest
from pydantic import ValidationError

from fgo_pet_content.packs.models import PackManifestV1


def _manifest(**overrides: object) -> dict:
    value = {
        "schema_version": 1,
        "package_id": "official.mash",
        "package_version": "1.0.0",
        "servant_id": "mash_kyrielight",
        "display_name": "玛修",
        "publisher": "community",
        "min_app_version": "1.0.0",
        "capabilities": ["art.v3"],
        "preview_path": "previews/library.png",
        "appearances": [
            {"appearance_id": "casual", "manifest_path": "appearances/casual/manifest.json"}
        ],
        "files": ["previews/library.png", "appearances/casual/manifest.json"],
    }
    value.update(overrides)
    return value


def test_pack_manifest_accepts_versioned_capabilities_and_explicit_files() -> None:
    manifest = PackManifestV1.model_validate(_manifest())

    assert manifest.schema_version == 1
    assert manifest.capabilities == ("art.v3",)
    assert manifest.files == ("previews/library.png", "appearances/casual/manifest.json")


def test_pack_manifest_keeps_capabilities_optional_for_legacy_metadata() -> None:
    value = _manifest()
    value.pop("capabilities")

    manifest = PackManifestV1.model_validate(value)

    assert manifest.capabilities == ()


@pytest.mark.parametrize(
    "field,value",
    [
        ("package_version", "1.0"),
        ("min_app_version", "latest"),
        ("capabilities", ["shell.exec"]),
        ("preview_path", "C:/private/library.png"),
        ("files", ["../outside.txt"]),
        ("files", ["runtime\\body.png"]),
    ],
)
def test_pack_manifest_rejects_invalid_contract_values(field: str, value: object) -> None:
    with pytest.raises(ValidationError):
        PackManifestV1.model_validate(_manifest(**{field: value}))


def test_pack_manifest_rejects_unknown_fields_and_duplicate_normalized_files() -> None:
    with pytest.raises(ValidationError):
        PackManifestV1.model_validate(_manifest(unexpected=True))

    with pytest.raises(ValidationError):
        PackManifestV1.model_validate(
            _manifest(files=["previews/library.png", "previews/library.png"])
        )
