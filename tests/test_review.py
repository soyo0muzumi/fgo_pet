import pytest

from fgo_pet_content.models.evidence import EvidenceCard, EvidenceCitation
from fgo_pet_content.models.source import (
    Authority,
    Region,
    ReviewStatus,
    TranslationStatus,
)
from fgo_pet_content.review import review_card


def make_card(
    *, authority: Authority = Authority.CORE, script_id: str = "script-1"
) -> EvidenceCard:
    return EvidenceCard(
        evidence_id=f"ev-{script_id}",
        subject="mash",
        category="personality",
        claim="玛修重视同伴的状态",
        authority=authority,
        confidence=0.9,
        translation_status=TranslationStatus.OFFICIAL_CN,
        sources=[
            EvidenceCitation(
                region=Region.CN,
                script_id=script_id,
                scene_index=1,
                utterance_orders=[1],
            )
        ],
    )


def test_pending_card_can_be_approved() -> None:
    reviewed = review_card(make_card(), ReviewStatus.APPROVED, notes="出处已核对")

    assert reviewed.review.status is ReviewStatus.APPROVED
    assert reviewed.review.notes == "出处已核对"


def test_authority_change_requires_review_notes() -> None:
    with pytest.raises(ValueError, match="notes"):
        review_card(
            make_card(authority=Authority.FLAVOR),
            ReviewStatus.APPROVED,
            notes="",
            authority=Authority.CORE,
        )
