from __future__ import annotations

from dataclasses import dataclass

from ..models.evidence import EvidenceCard
from .context import EvidenceWindow


@dataclass(frozen=True, slots=True)
class ValidationResult:
    accepted: bool
    reasons: tuple[str, ...]


def validate_evidence(
    card: EvidenceCard, window: EvidenceWindow
) -> ValidationResult:
    reasons: list[str] = []
    allowed_orders = {item.order for item in window.utterances}
    for citation in card.sources:
        if (
            citation.region != window.source.region
            or citation.script_id != window.source.script_id
            or citation.scene_index != window.scene_index
            or not set(citation.utterance_orders).issubset(allowed_orders)
        ):
            reasons.append("outside supplied evidence")
            break
    normalized_claim = _normalize(card.claim)
    if normalized_claim and any(
        normalized_claim == _normalize(item.text) for item in window.utterances
    ):
        reasons.append("claim copies raw dialogue instead of abstracting it")
    return ValidationResult(accepted=not reasons, reasons=tuple(reasons))


def _normalize(value: str) -> str:
    return "".join(value.split()).strip("。！？!?.,，")
