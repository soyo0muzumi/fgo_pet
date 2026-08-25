from __future__ import annotations

from pydantic import BaseModel, Field

from ..models.evidence import EvidenceCard
from .context import EvidenceWindow
from .prompts import SYSTEM_PROMPT, render_window
from .validator import validate_evidence


class EvidenceBatch(BaseModel):
    cards: list[EvidenceCard] = Field(default_factory=list)


class EvidenceValidationError(ValueError):
    pass


class EvidenceExtractor:
    def __init__(self, structured_client) -> None:
        self._client = structured_client

    def extract(self, window: EvidenceWindow) -> list[EvidenceCard]:
        payload = self._client.generate(
            system=SYSTEM_PROMPT,
            user=render_window(window),
            schema=EvidenceBatch.model_json_schema(),
        )
        batch = EvidenceBatch.model_validate(payload)
        for card in batch.cards:
            result = validate_evidence(card, window)
            if not result.accepted:
                raise EvidenceValidationError("; ".join(result.reasons))
        return batch.cards
