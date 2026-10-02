from __future__ import annotations

import math
from pathlib import Path
from typing import Protocol

from pydantic import BaseModel, ConfigDict

from ..profile import MashProfile
from .index import load_adjacent_story_hits, search_story_index
from .models import StoryHit
from .query import route_query


class StoryReranker(Protocol):
    def rerank(self, query: str, hits: list[StoryHit]) -> list[StoryHit]: ...


class RuntimeContext(BaseModel):
    model_config = ConfigDict(extra="forbid")

    route: str
    route_reasons: tuple[str, ...]
    profile_summary: str
    story_windows: list[StoryHit]
    estimated_tokens: int
    coverage_gap: bool
    reranker_status: str
    answer_sentence_range: tuple[int, int] = (2, 4)
    expand_on_request: bool = True
    unsupported_detail_policy: str = (
        "说明现有资料覆盖不足，不把资料缺口表述为角色本人无知。"
    )


def _estimate_tokens(text: str) -> int:
    return max(1, math.ceil(len(text) / 2))


def compose_context(
    query: str,
    profile: MashProfile,
    database: Path,
    *,
    reranker: StoryReranker | None = None,
    token_budget: int = 900,
) -> RuntimeContext:
    decision = route_query(query, profile)
    profile_tokens = _estimate_tokens(profile.summary)
    if decision.route != "story":
        return RuntimeContext(
            route=decision.route,
            route_reasons=decision.reasons,
            profile_summary=profile.summary,
            story_windows=[],
            estimated_tokens=profile_tokens,
            coverage_gap=False,
            reranker_status="unused",
        )

    hits = search_story_index(database, query, limit=8)
    reranker_status = "unused"
    if reranker is not None and hits:
        try:
            hits = reranker.rerank(query, hits)
            reranker_status = "applied"
        # Rerankers are optional plugin boundaries; any failure must preserve FTS results.
        except Exception:
            reranker_status = "fallback"

    if len(hits) == 1:
        hits = [hits[0], *load_adjacent_story_hits(database, hits[0])]

    selected: list[StoryHit] = []
    seen: set[str] = set()
    estimated_tokens = profile_tokens
    for hit in hits:
        if hit.scene_id in seen:
            continue
        window_tokens = _estimate_tokens(hit.text)
        if estimated_tokens + window_tokens > token_budget:
            continue
        selected.append(hit)
        seen.add(hit.scene_id)
        estimated_tokens += window_tokens
        if len(selected) == 4:
            break

    return RuntimeContext(
        route=decision.route,
        route_reasons=decision.reasons,
        profile_summary=profile.summary,
        story_windows=selected,
        estimated_tokens=estimated_tokens,
        coverage_gap=len(selected) < 2,
        reranker_status=reranker_status,
    )
