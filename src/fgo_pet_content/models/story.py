from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from .source import SourceRef


class CastMember(BaseModel):
    model_config = ConfigDict(extra="forbid")

    display_name: str
    figure_id: str | None = None
    servant_id: int | None = None


class Utterance(BaseModel):
    model_config = ConfigDict(extra="forbid")

    order: int
    speaker: str
    actor_slot: str | None = None
    servant_id: int | None = None
    figure_id: str | None = None
    face_id: int | None = None
    text: str
    branch_path: list[str] = Field(default_factory=list)
    raw_start_line: int
    raw_end_line: int


class UnknownCommand(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    arguments: list[str] = Field(default_factory=list)
    line_number: int
    raw: str


class StoryScene(BaseModel):
    model_config = ConfigDict(extra="forbid")

    scene_index: int
    background_id: str | None = None
    utterances: list[Utterance] = Field(default_factory=list)


class StoryDocument(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schema_version: Literal[1] = 1
    source: SourceRef
    cast: dict[str, CastMember] = Field(default_factory=dict)
    scenes: list[StoryScene] = Field(default_factory=list)
    unknown_commands: list[UnknownCommand] = Field(default_factory=list)
