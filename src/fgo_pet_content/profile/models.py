from typing import Literal

from pydantic import BaseModel, ConfigDict

from ..models.source import Region


class ProfileFact(BaseModel):
    model_config = ConfigDict(extra="forbid")

    value: str
    source_region: Region
    source_path: str
    jp_fallback: bool = False


class MashProfile(BaseModel):
    model_config = ConfigDict(extra="forbid")

    servant_id: Literal[800100]
    collection_no: Literal[1]
    name: str
    summary: str
    facts: dict[str, ProfileFact]
    source_hashes: dict[str, str]


class ProfileUnavailable(RuntimeError):
    pass
