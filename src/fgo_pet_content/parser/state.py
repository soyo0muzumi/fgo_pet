from __future__ import annotations

from dataclasses import dataclass, field

from ..models.story import CastMember, StoryScene


@dataclass(slots=True)
class ParserState:
    cast: dict[str, CastMember] = field(default_factory=dict)
    faces: dict[str, int] = field(default_factory=dict)
    scenes: list[StoryScene] = field(default_factory=list)
    current_talk_slot: str | None = None
    branch_path: list[str] = field(default_factory=list)

    def start_scene(self, background_id: str | None) -> StoryScene:
        scene = StoryScene(
            scene_index=len(self.scenes) + 1,
            background_id=background_id,
        )
        self.scenes.append(scene)
        return scene

    def current_scene(self) -> StoryScene:
        if not self.scenes:
            return self.start_scene(None)
        return self.scenes[-1]
