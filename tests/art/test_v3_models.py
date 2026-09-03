import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from fgo_pet_content.art.v3_models import (
    ArtManifestV3,
    CORE_EXPRESSION_SEMANTICS,
)


FIXTURE = Path("tests/fixtures/packs/mash-art-v3.json")


def _payload() -> dict[str, object]:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


def test_mash_v3_fixture_is_strict_and_complete() -> None:
    manifest = ArtManifestV3.model_validate(_payload())

    assert manifest.schema_version == 3
    assert manifest.appearance_id == "casual"
    assert len(manifest.assets) == 29
    assert set(manifest.expression_semantics) == set(CORE_EXPRESSION_SEMANTICS)


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda payload: payload.update(unexpected=True), "Extra inputs"),
        (
            lambda payload: payload["assets"].append(payload["assets"][0].copy()),
            "duplicate stable_id",
        ),
        (
            lambda payload: payload["composition"].update(default_scale=0.7),
            "default_scale",
        ),
        (
            lambda payload: payload["expression_semantics"].pop("neutral"),
            "neutral",
        ),
        (
            lambda payload: payload["expression_semantics"].update(happy="unknown"),
            "unknown expression asset",
        ),
        (
            lambda payload: payload["fallback"].update(happy="sad", sad="happy"),
            "fallback cycle",
        ),
    ],
)
def test_v3_rejects_contract_violations(mutate, message: str) -> None:
    payload = _payload()
    mutate(payload)

    with pytest.raises(ValidationError, match=message):
        ArtManifestV3.model_validate(payload)
