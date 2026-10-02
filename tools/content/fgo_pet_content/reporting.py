from __future__ import annotations

from collections.abc import Mapping, Sequence

from .models.evidence import EvidenceCard


def build_review_report(
    cards: Sequence[EvidenceCard],
    *,
    unknown_commands: Mapping[str, int] | None = None,
) -> dict:
    return {
        "evidence": [
            {
                "evidence_id": card.evidence_id,
                "claim": card.claim,
                "authority": card.authority.value,
                "confidence": card.confidence,
                "review_status": card.review.status.value,
                "sources": [
                    {
                        "region": source.region.value,
                        "script_id": source.script_id,
                        "scene_index": source.scene_index,
                        "utterance_orders": source.utterance_orders,
                    }
                    for source in card.sources
                ],
            }
            for card in cards
        ],
        "unknown_commands": dict(sorted((unknown_commands or {}).items())),
    }
