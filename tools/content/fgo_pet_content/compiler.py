from __future__ import annotations

import json
from dataclasses import asdict
from dataclasses import dataclass
from pathlib import Path

from .cache import atomic_write
from .models.evidence import EvidenceCard
from .models.source import Authority, ReviewStatus


REVIEW_ARTIFACT_FIELDS = {
    "chapter",
    "quote",
    "original_quote",
    "summary",
    "context_note",
}


@dataclass(frozen=True, slots=True)
class SupportSummary:
    claim: str
    evidence_ids: tuple[str, ...]
    independent_source_count: int


@dataclass(frozen=True, slots=True)
class PersonaBundle:
    core_evidence: tuple[EvidenceCard, ...]
    style_evidence: tuple[EvidenceCard, ...]
    knowledge_evidence: tuple[EvidenceCard, ...]
    support: tuple[SupportSummary, ...]


def load_evidence_cards(path: Path) -> list[EvidenceCard]:
    cards = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        payload = json.loads(line)
        for field in REVIEW_ARTIFACT_FIELDS:
            payload.pop(field, None)
        cards.append(EvidenceCard.model_validate(payload))
    return cards


def merge_support(cards: list[EvidenceCard]) -> list[SupportSummary]:
    grouped: dict[str, list[EvidenceCard]] = {}
    for card in cards:
        grouped.setdefault(_claim_key(card.claim), []).append(card)
    return [
        SupportSummary(
            claim=items[0].claim,
            evidence_ids=tuple(sorted(item.evidence_id for item in items)),
            independent_source_count=len(
                {
                    citation.script_id
                    for item in items
                    for citation in item.sources
                }
            ),
        )
        for _, items in sorted(grouped.items())
    ]


def compile_persona(cards: list[EvidenceCard]) -> PersonaBundle:
    approved = sorted(
        (card for card in cards if card.review.status is ReviewStatus.APPROVED),
        key=lambda card: card.evidence_id,
    )
    core = tuple(card for card in approved if card.authority is Authority.CORE)
    style = tuple(card for card in approved if card.authority is Authority.STYLE)
    knowledge = tuple(
        card
        for card in approved
        if card.authority in {Authority.CONTEXT, Authority.FLAVOR}
    )
    return PersonaBundle(
        core_evidence=core,
        style_evidence=style,
        knowledge_evidence=knowledge,
        support=tuple(merge_support(approved)),
    )


def write_persona_bundle(
    bundle: PersonaBundle, output_dir: Path
) -> dict[str, Path]:
    outputs = {
        "core_persona": output_dir / "persona" / "core_persona.json",
        "speech_style": output_dir / "persona" / "speech_style.json",
        "knowledge": output_dir / "knowledge" / "topics.jsonl",
        "support": output_dir / "persona" / "support.json",
    }
    atomic_write(
        outputs["core_persona"],
        _cards_json(bundle.core_evidence).encode("utf-8"),
    )
    atomic_write(
        outputs["speech_style"],
        _cards_json(bundle.style_evidence).encode("utf-8"),
    )
    knowledge_text = "\n".join(
        card.model_dump_json() for card in bundle.knowledge_evidence
    )
    atomic_write(outputs["knowledge"], knowledge_text.encode("utf-8"))
    atomic_write(
        outputs["support"],
        json.dumps(
            [asdict(item) for item in bundle.support],
            ensure_ascii=False,
            indent=2,
        ).encode("utf-8"),
    )
    return outputs


def _claim_key(claim: str) -> str:
    return "".join(claim.split()).strip("。！？!?.,，")


def _cards_json(cards: tuple[EvidenceCard, ...]) -> str:
    return json.dumps(
        [card.model_dump(mode="json") for card in cards],
        ensure_ascii=False,
        indent=2,
    )
