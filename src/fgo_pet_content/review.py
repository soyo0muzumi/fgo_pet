from __future__ import annotations

from .models.evidence import EvidenceCard, ReviewState
from .models.source import Authority, ReviewStatus


def review_card(
    card: EvidenceCard,
    status: ReviewStatus,
    *,
    notes: str,
    authority: Authority | None = None,
) -> EvidenceCard:
    if card.review.status is not ReviewStatus.PENDING:
        raise ValueError("only pending evidence can be reviewed")
    if status is ReviewStatus.PENDING:
        raise ValueError("review must approve or reject the evidence")
    if authority is not None and authority is not card.authority and not notes.strip():
        raise ValueError("authority changes require review notes")
    return card.model_copy(
        update={
            "authority": authority or card.authority,
            "review": ReviewState(status=status, notes=notes.strip() or None),
        },
        deep=True,
    )
