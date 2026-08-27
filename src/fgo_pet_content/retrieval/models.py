from pydantic import BaseModel, ConfigDict

from ..models.source import SourceRef


class StoryIndexManifest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schema_version: int = 1
    scene_count: int


class StoryHit(BaseModel):
    model_config = ConfigDict(extra="forbid")

    scene_id: str
    scene_index: int
    source: SourceRef
    speakers: tuple[str, ...]
    text: str
    score: float
