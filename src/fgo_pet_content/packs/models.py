from __future__ import annotations

import re
from typing import Literal
from pathlib import PurePosixPath

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


KNOWN_CAPABILITIES = frozenset(
    {
        "art.v3",
        "dialogue.v1",
        "knowledge.v1",
        "persona.v1",
    }
)
_ID_PATTERN = r"^[a-z0-9][a-z0-9_.-]{0,63}$"
_SEMVER_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)


def is_safe_relative_path(value: str) -> bool:
    if not value or "\\" in value or value.startswith("/"):
        return False
    if re.match(r"^[A-Za-z]:($|/)", value):
        return False
    path = PurePosixPath(value)
    return (
        not path.is_absolute()
        and all(part not in {"", ".", ".."} for part in path.parts)
        and path.as_posix() == value
    )


class PackAppearanceRef(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    appearance_id: str = Field(pattern=_ID_PATTERN)
    manifest_path: str = Field(min_length=1)

    @field_validator("manifest_path")
    @classmethod
    def validate_manifest_path(cls, value: str) -> str:
        if not is_safe_relative_path(value):
            raise ValueError("manifest_path must be a safe relative POSIX path")
        return value


class PackManifestV1(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    schema_version: Literal[1]
    package_id: str = Field(pattern=_ID_PATTERN)
    package_version: str = Field(min_length=1)
    servant_id: str = Field(pattern=_ID_PATTERN)
    display_name: str = Field(min_length=1, max_length=160)
    publisher: str = Field(default="", max_length=160)
    min_app_version: str = ""
    capabilities: tuple[str, ...] = ()
    preview_path: str = Field(min_length=1)
    appearances: tuple[PackAppearanceRef, ...] = Field(min_length=1)
    files: tuple[str, ...] = Field(default=())

    @field_validator("package_version", "min_app_version")
    @classmethod
    def validate_semver(cls, value: str, info) -> str:
        if value and not _SEMVER_PATTERN.fullmatch(value):
            raise ValueError(f"{info.field_name} must be a valid SemVer")
        return value

    @field_validator("preview_path", "files")
    @classmethod
    def validate_paths(cls, value):
        values = (value,) if isinstance(value, str) else value
        for path in values:
            if not is_safe_relative_path(path):
                raise ValueError("paths must be safe relative POSIX paths")
        return value

    @field_validator("capabilities")
    @classmethod
    def validate_capabilities(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        if len(value) != len(set(value)):
            raise ValueError("capabilities cannot contain duplicates")
        unknown = set(value) - KNOWN_CAPABILITIES
        if unknown:
            raise ValueError("unknown capability: " + ", ".join(sorted(unknown)))
        return value

    @model_validator(mode="after")
    def validate_references(self) -> PackManifestV1:
        appearance_ids = [item.appearance_id for item in self.appearances]
        if len(appearance_ids) != len(set(appearance_ids)):
            raise ValueError("appearances cannot contain duplicate appearance_id")
        if "package.json" in self.files:
            raise ValueError("files must not include package.json")
        if len(self.files) != len(set(self.files)):
            raise ValueError("files cannot contain duplicate paths")
        if self.files:
            required = {self.preview_path} | {
                appearance.manifest_path for appearance in self.appearances
            }
            missing = required - set(self.files)
            if missing:
                raise ValueError(
                    "files must declare preview and appearance manifests: "
                    + ", ".join(sorted(missing))
                )
        return self


class PackValidationIssue(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    check_id: str
    path: str | None = None
    detail: str


class PackValidationReport(BaseModel):
    model_config = ConfigDict(extra="forbid")

    status: Literal["PASS", "FAIL"]
    errors: list[PackValidationIssue]
    warnings: list[PackValidationIssue]
    declared_files: tuple[str, ...] = ()
    manifest: PackManifestV1 | None = Field(default=None, exclude=True, repr=False)
