from __future__ import annotations

from dataclasses import dataclass

from .models.source import Region, TranslationStatus
from .models.story import StoryDocument, Utterance


@dataclass(frozen=True, slots=True)
class AlignmentPair:
    scene_index: int
    cn_order: int
    jp_order: int
    status: TranslationStatus


@dataclass(frozen=True, slots=True)
class UnmatchedUtterance:
    region: Region
    scene_index: int
    order: int


@dataclass(frozen=True, slots=True)
class AlignmentDocument:
    script_id: str
    status: TranslationStatus
    confidence: float
    pairs: tuple[AlignmentPair, ...]
    unmatched: tuple[UnmatchedUtterance, ...]


def align_documents(
    cn: StoryDocument, jp: StoryDocument
) -> AlignmentDocument:
    if cn.source.script_id != jp.source.script_id:
        raise ValueError("alignment requires the same script ID")
    if cn.source.region is not Region.CN or jp.source.region is not Region.JP:
        raise ValueError("alignment requires CN followed by JP documents")

    pairs: list[AlignmentPair] = []
    unmatched: list[UnmatchedUtterance] = []
    jp_scenes = {scene.scene_index: scene for scene in jp.scenes}
    for cn_scene in cn.scenes:
        jp_scene = jp_scenes.pop(cn_scene.scene_index, None)
        if jp_scene is None:
            unmatched.extend(
                UnmatchedUtterance(Region.CN, cn_scene.scene_index, item.order)
                for item in cn_scene.utterances
            )
            continue
        scene_pairs, cn_missing, jp_missing = _align_utterances(
            cn_scene.utterances, jp_scene.utterances
        )
        pairs.extend(
            AlignmentPair(
                scene_index=cn_scene.scene_index,
                cn_order=cn_order,
                jp_order=jp_order,
                status=TranslationStatus.OFFICIAL_CN,
            )
            for cn_order, jp_order in scene_pairs
        )
        unmatched.extend(
            UnmatchedUtterance(Region.CN, cn_scene.scene_index, order)
            for order in cn_missing
        )
        unmatched.extend(
            UnmatchedUtterance(Region.JP, jp_scene.scene_index, order)
            for order in jp_missing
        )
    for jp_scene in jp_scenes.values():
        unmatched.extend(
            UnmatchedUtterance(Region.JP, jp_scene.scene_index, item.order)
            for item in jp_scene.utterances
        )

    if not unmatched:
        status = TranslationStatus.OFFICIAL_CN
    elif len(unmatched) <= 1:
        status = TranslationStatus.ALIGNMENT_UNCERTAIN
    else:
        status = TranslationStatus.CN_JP_DIVERGENCE
    total = len(pairs) + len(unmatched)
    confidence = len(pairs) / total if total else 1.0
    return AlignmentDocument(
        script_id=cn.source.script_id,
        status=status,
        confidence=confidence,
        pairs=tuple(pairs),
        unmatched=tuple(unmatched),
    )


def _align_utterances(
    cn_items: list[Utterance], jp_items: list[Utterance]
) -> tuple[list[tuple[int, int]], list[int], list[int]]:
    cn_keys = [_actor_key(item) for item in cn_items]
    jp_keys = [_actor_key(item) for item in jp_items]
    lengths = [[0] * (len(jp_keys) + 1) for _ in range(len(cn_keys) + 1)]
    for cn_index, cn_key in enumerate(cn_keys, start=1):
        for jp_index, jp_key in enumerate(jp_keys, start=1):
            if cn_key == jp_key:
                lengths[cn_index][jp_index] = lengths[cn_index - 1][jp_index - 1] + 1
            else:
                lengths[cn_index][jp_index] = max(
                    lengths[cn_index - 1][jp_index],
                    lengths[cn_index][jp_index - 1],
                )

    matches: list[tuple[int, int]] = []
    cn_index, jp_index = len(cn_items), len(jp_items)
    while cn_index and jp_index:
        if cn_keys[cn_index - 1] == jp_keys[jp_index - 1]:
            matches.append((cn_items[cn_index - 1].order, jp_items[jp_index - 1].order))
            cn_index -= 1
            jp_index -= 1
        elif lengths[cn_index - 1][jp_index] >= lengths[cn_index][jp_index - 1]:
            cn_index -= 1
        else:
            jp_index -= 1
    matches.reverse()
    matched_cn = {item[0] for item in matches}
    matched_jp = {item[1] for item in matches}
    return (
        matches,
        [item.order for item in cn_items if item.order not in matched_cn],
        [item.order for item in jp_items if item.order not in matched_jp],
    )


def _actor_key(utterance: Utterance) -> tuple[str, str | int]:
    if utterance.servant_id is not None:
        return ("servant", utterance.servant_id)
    if utterance.actor_slot is not None:
        return ("slot", utterance.actor_slot)
    return ("speaker", utterance.speaker)
