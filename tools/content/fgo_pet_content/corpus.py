from __future__ import annotations

from dataclasses import dataclass, replace

from .models.source import Region


@dataclass(frozen=True, slots=True)
class StoryArc:
    war_id: int
    slug: str
    display_name: str


@dataclass(frozen=True, slots=True)
class WarScript:
    war_id: int
    war_name: str
    script_id: str
    script_url: str
    quest_id: int | None = None
    quest_name: str | None = None
    chapter_id: int | None = None
    chapter_sub_id: int | None = None
    chapter_sub_str: str = ""
    phases: tuple[int, ...] = ()


@dataclass(frozen=True, slots=True)
class RegionalWarScript:
    region: Region
    script: WarScript


DEFAULT_STORY_ARCS = (
    StoryArc(100, "singularity-f", "特异点F"),
    StoryArc(107, "singularity-7", "第七特异点"),
    StoryArc(108, "final-singularity", "终局特异点"),
    StoryArc(300, "part-2-prologue", "第二部序"),
    StoryArc(308, "lostbelt-6", "Lostbelt No.6"),
    StoryArc(311, "lostbelt-7", "Lostbelt No.7"),
    StoryArc(405, "ordeal-call-4", "奏章Ⅳ"),
    StoryArc(407, "part-2-finale", "第二部终章"),
)


def enumerate_war_scripts(payload: dict) -> list[WarScript]:
    war_id = int(payload["id"])
    war_name = payload.get("longName") or payload.get("name") or str(war_id)
    ordered: list[WarScript] = []
    by_id: dict[str, int] = {}

    opening_id = payload.get("scriptId")
    opening_url = payload.get("script")
    if opening_id not in {None, "", "0", "NONE"} and opening_url:
        by_id[opening_id] = len(ordered)
        ordered.append(WarScript(war_id, war_name, opening_id, opening_url))

    for spot in payload.get("spots", []):
        for quest in spot.get("quests", []):
            for phase_group in quest.get("phaseScripts", []):
                phase = int(phase_group["phase"])
                for link in phase_group.get("scripts", []):
                    script_id = link["scriptId"]
                    if script_id in by_id:
                        index = by_id[script_id]
                        current = ordered[index]
                        if current.quest_id == quest.get("id"):
                            ordered[index] = replace(
                                current,
                                phases=tuple(dict.fromkeys((*current.phases, phase))),
                            )
                        continue
                    by_id[script_id] = len(ordered)
                    ordered.append(
                        WarScript(
                            war_id=war_id,
                            war_name=war_name,
                            script_id=script_id,
                            script_url=link["script"],
                            quest_id=quest.get("id"),
                            quest_name=quest.get("name"),
                            chapter_id=quest.get("chapterId"),
                            chapter_sub_id=quest.get("chapterSubId"),
                            chapter_sub_str=quest.get("chapterSubStr", ""),
                            phases=(phase,),
                        )
                    )
    return ordered


def merge_region_scripts(
    cn_scripts: list[WarScript], jp_scripts: list[WarScript]
) -> list[RegionalWarScript]:
    cn_by_id = {item.script_id: item for item in cn_scripts}
    jp_ids = {item.script_id for item in jp_scripts}
    merged = [
        RegionalWarScript(
            Region.CN if item.script_id in cn_by_id else Region.JP,
            cn_by_id.get(item.script_id, item),
        )
        for item in jp_scripts
    ]
    merged.extend(
        RegionalWarScript(Region.CN, item)
        for item in cn_scripts
        if item.script_id not in jp_ids
    )
    return merged


def load_regional_scripts(atlas, war_id: int) -> list[RegionalWarScript]:
    cn_payload = atlas.fetch_war(Region.CN, war_id)
    jp_payload = atlas.fetch_war(Region.JP, war_id)
    cn_scripts = enumerate_war_scripts(cn_payload) if cn_payload else []
    jp_scripts = enumerate_war_scripts(jp_payload) if jp_payload else []
    return merge_region_scripts(cn_scripts, jp_scripts)
