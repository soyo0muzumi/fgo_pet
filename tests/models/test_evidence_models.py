import pytest
from pydantic import ValidationError

from fgo_pet_content.models.evidence import EvidenceCard, EvidenceCitation
from fgo_pet_content.models.source import Authority, Region, TranslationStatus


def test_evidence_requires_a_precise_citation() -> None:
    with pytest.raises(ValidationError):
        EvidenceCard(
            evidence_id="ev-1",
            subject="mash",
            category="relationship",
            claim="玛修信赖前辈",
            authority=Authority.CORE,
            confidence=0.9,
            translation_status=TranslationStatus.OFFICIAL_CN,
            sources=[],
        )


def test_citation_rejects_empty_utterance_orders() -> None:
    with pytest.raises(ValidationError):
        EvidenceCitation(
            region=Region.CN,
            script_id="0200040010",
            scene_index=1,
            utterance_orders=[],
        )


def test_confidence_must_be_normalized() -> None:
    citation = EvidenceCitation(
        region=Region.CN,
        script_id="0200040010",
        scene_index=1,
        utterance_orders=[2],
    )

    with pytest.raises(ValidationError):
        EvidenceCard(
            evidence_id="ev-2",
            subject="mash",
            category="style",
            claim="使用礼貌称呼",
            authority=Authority.STYLE,
            confidence=1.5,
            translation_status=TranslationStatus.OFFICIAL_CN,
            sources=[citation],
        )
