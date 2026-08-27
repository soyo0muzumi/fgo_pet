from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field

from .source import Authority, Region, ReviewStatus, TranslationStatus


class EvidenceCitation(BaseModel):
    model_config = ConfigDict(extra="forbid")

    region: Region
    script_id: str
    scene_index: int
    utterance_orders: Annotated[list[int], Field(min_length=1)]


class ReviewState(BaseModel):
    model_config = ConfigDict(extra="forbid")

    status: ReviewStatus = ReviewStatus.PENDING
    notes: str | None = None


class EvidenceCard(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schema_version: Literal[1] = 1
    evidence_id: str
    subject: str
    category: str
    claim: str
    conditions: list[str] = Field(default_factory=list)
    behavior: list[str] = Field(default_factory=list)
    speech_traits: list[str] = Field(default_factory=list)
    timeline: str | None = None
    authority: Authority
    confidence: float = Field(ge=0, le=1)
    translation_status: TranslationStatus
    sources: Annotated[list[EvidenceCitation], Field(min_length=1)]
    review: ReviewState = Field(default_factory=ReviewState)
