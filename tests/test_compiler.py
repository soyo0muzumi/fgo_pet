import json

from fgo_pet_content.compiler import compile_persona, merge_support, write_persona_bundle
from fgo_pet_content.models.source import Authority, ReviewStatus
from fgo_pet_content.review import review_card

from .test_review import make_card


def test_only_approved_core_cards_enter_core_persona() -> None:
    approved = review_card(make_card(), ReviewStatus.APPROVED, notes="已核对")
    pending = make_card(script_id="script-2")

    bundle = compile_persona([approved, pending])

    assert [item.evidence_id for item in bundle.core_evidence] == [approved.evidence_id]
    assert all(item.authority is Authority.CORE for item in bundle.core_evidence)


def test_same_script_does_not_count_as_independent_support() -> None:
    first = make_card()
    second = make_card()
    second.evidence_id = "ev-second"
    second.claim = first.claim

    merged = merge_support([first, second])

    assert merged[0].independent_source_count == 1


def test_persona_bundle_writes_separate_runtime_layers(tmp_path) -> None:
    core = review_card(make_card(), ReviewStatus.APPROVED, notes="已核对")
    style = review_card(
        make_card(authority=Authority.STYLE, script_id="style-script"),
        ReviewStatus.APPROVED,
        notes="已核对",
    )

    outputs = write_persona_bundle(compile_persona([core, style]), tmp_path)

    assert set(outputs) == {"core_persona", "speech_style", "knowledge", "support"}
    core_payload = json.loads(outputs["core_persona"].read_text(encoding="utf-8"))
    assert core_payload[0]["evidence_id"] == core.evidence_id
