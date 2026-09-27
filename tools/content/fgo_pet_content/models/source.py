from enum import StrEnum

from pydantic import BaseModel, ConfigDict


class Region(StrEnum):
    CN = "CN"
    JP = "JP"


class TranslationStatus(StrEnum):
    OFFICIAL_CN = "official_cn"
    JP_FALLBACK = "jp_fallback"
    ALIGNMENT_UNCERTAIN = "alignment_uncertain"
    CN_JP_DIVERGENCE = "cn_jp_divergence"


class Authority(StrEnum):
    CORE = "core"
    CONTEXT = "context"
    STYLE = "style"
    FLAVOR = "flavor"
    ARCHIVE = "archive"


class ReviewStatus(StrEnum):
    PENDING = "pending"
    APPROVED = "approved"
    REJECTED = "rejected"


class SourceRef(BaseModel):
    model_config = ConfigDict(extra="forbid")

    region: Region
    script_id: str
    container_type: str
    container_id: int | None = None
    container_name: str | None = None
    content_hash: str | None = None
    source_url: str | None = None
    data_version: str | None = None
