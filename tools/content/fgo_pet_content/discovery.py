from __future__ import annotations

from dataclasses import dataclass, field

from .models.source import Region
from .models.story import StoryDocument


@dataclass(frozen=True, slots=True)
class MashIdentity:
    servant_id: int
    names: dict[Region, tuple[str, ...]]
    figure_ids: frozenset[str]
    script_whitelist: frozenset[str] = frozenset()

    @classmethod
    def default(cls) -> MashIdentity:
        return cls(
            servant_id=800100,
            names={Region.CN: ("玛修",), Region.JP: ("マシュ",)},
            figure_ids=frozenset({"98001000"}),
        )


@dataclass(slots=True)
class ScriptCandidate:
    script_id: str
    matched_regions: set[Region] = field(default_factory=set)
    match_reasons: set[str] = field(default_factory=set)
    script_urls: dict[Region, str] = field(default_factory=dict)
    best_score: float = 0.0


def discover_candidates(identity: MashIdentity, atlas) -> list[ScriptCandidate]:
    candidates: dict[str, ScriptCandidate] = {}
    for region, names in identity.names.items():
        for name in names:
            for hit in atlas.search_scripts(region, name):
                candidate = candidates.setdefault(
                    hit.script_id, ScriptCandidate(script_id=hit.script_id)
                )
                candidate.matched_regions.add(region)
                candidate.match_reasons.add(f"name:{name}")
                candidate.script_urls[region] = hit.script_url
                candidate.best_score = max(candidate.best_score, hit.score)
    return sorted(candidates.values(), key=lambda item: (-item.best_score, item.script_id))


def is_mash_related(document: StoryDocument, identity: MashIdentity) -> bool:
    known_names = {name for names in identity.names.values() for name in names}
    if document.source.script_id in identity.script_whitelist:
        return True
    return any(
        utterance.servant_id == identity.servant_id
        or utterance.speaker in known_names
        for scene in document.scenes
        for utterance in scene.utterances
    )
