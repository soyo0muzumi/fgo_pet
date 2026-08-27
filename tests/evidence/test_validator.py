from fgo_pet_content.evidence.context import build_evidence_windows
from fgo_pet_content.evidence.validator import validate_evidence
from fgo_pet_content.models.evidence import EvidenceCard, EvidenceCitation
from fgo_pet_content.models.source import Authority, Region, TranslationStatus

from .test_context import make_document


def make_card(order: int = 4, claim: str = "玛修会主动支持同伴") -> EvidenceCard:
    return EvidenceCard(
        evidence_id="ev-1",
        subject="mash",
        category="behavior",
        claim=claim,
        authority=Authority.CONTEXT,
        confidence=0.8,
        translation_status=TranslationStatus.OFFICIAL_CN,
        sources=[
            EvidenceCitation(
                region=Region.CN,
                script_id="script-1",
                scene_index=1,
                utterance_orders=[order],
            )
        ],
    )


def test_validator_rejects_citation_outside_window() -> None:
    window = build_evidence_windows(make_document(), 800100, 3)[0]

    result = validate_evidence(make_card(order=999), window)

    assert not result.accepted
    assert "outside supplied evidence" in result.reasons


def test_validator_accepts_source_bound_abstract_claim() -> None:
    window = build_evidence_windows(make_document(), 800100, 3)[0]

    result = validate_evidence(make_card(), window)

    assert result.accepted
