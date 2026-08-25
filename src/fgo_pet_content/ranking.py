from __future__ import annotations

from dataclasses import dataclass, replace

from .models.story import StoryDocument


@dataclass(frozen=True, slots=True)
class ChapterMetrics:
    script_id: str
    region: str
    container_type: str
    container_id: int | None
    container_name: str | None
    category: str
    mash_utterance_count: int
    total_utterance_count: int
    unique_mash_faces: int
    atlas_score: float
    selection_score: float = 0.0


def measure_chapter(
    document: StoryDocument,
    *,
    atlas_score: float,
    category: str = "unclassified",
) -> ChapterMetrics:
    utterances = [
        utterance
        for scene in document.scenes
        for utterance in scene.utterances
    ]
    mash = [
        utterance
        for utterance in utterances
        if utterance.servant_id == 800100 or utterance.speaker in {"玛修", "マシュ"}
    ]
    return ChapterMetrics(
        script_id=document.source.script_id,
        region=document.source.region.value,
        container_type=document.source.container_type,
        container_id=document.source.container_id,
        container_name=document.source.container_name,
        category=category,
        mash_utterance_count=len(mash),
        total_utterance_count=len(utterances),
        unique_mash_faces=len(
            {utterance.face_id for utterance in mash if utterance.face_id is not None}
        ),
        atlas_score=atlas_score,
    )


def rank_chapters(items: list[ChapterMetrics]) -> list[ChapterMetrics]:
    scored = [replace(item, selection_score=_score(item)) for item in items]
    return sorted(scored, key=lambda item: (-item.selection_score, item.script_id))


def select_balanced(
    ranked: list[ChapterMetrics], quotas: dict[str, int]
) -> list[ChapterMetrics]:
    selected: list[ChapterMetrics] = []
    for category, limit in quotas.items():
        selected.extend(
            item
            for item in ranked
            if item.category == category
        )
        category_items = [item for item in selected if item.category == category]
        if len(category_items) > limit:
            rejected_ids = {item.script_id for item in category_items[limit:]}
            selected = [item for item in selected if item.script_id not in rejected_ids]
    return sorted(selected, key=lambda item: (-item.selection_score, item.script_id))


def _score(item: ChapterMetrics) -> float:
    density = (
        item.mash_utterance_count / item.total_utterance_count
        if item.total_utterance_count
        else 0.0
    )
    dialogue_value = min(item.mash_utterance_count, 80) / 80 * 35
    density_value = min(density, 0.6) / 0.6 * 25
    face_value = min(item.unique_mash_faces, 10) / 10 * 15
    search_value = min(item.atlas_score, 150) / 150 * 15
    source_value = {
        "war_opening": 10,
        "quest": 8,
        "interlude": 8,
        "event": 5,
    }.get(item.container_type, 2)
    return round(
        dialogue_value + density_value + face_value + search_value + source_value,
        3,
    )
