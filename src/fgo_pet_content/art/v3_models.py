from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from .models import Point, Size


CORE_EXPRESSION_SEMANTICS: tuple[str, ...] = (
    "neutral",
    "happy",
    "excited",
    "shy",
    "concerned",
    "sad",
    "surprised",
    "angry",
)
SUPPORTED_SCALES = frozenset({0.50, 0.60, 0.75})


class ArtAssetV3(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    asset_type: Literal["body", "expression"] = Field(alias="type")
    stable_id: str = Field(min_length=1)
    path: str = Field(min_length=1)
    sha256: str = Field(pattern=r"^sha256:[0-9a-f]{64}$")


class CompositionV3(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    body_id: str = Field(min_length=1)
    default_expression_id: str = Field(min_length=1)
    overlay_offset: Point
    overlay_size: Size
    panel_anchor: Point
    default_scale: float

    @model_validator(mode="after")
    def validate_scale(self) -> CompositionV3:
        if self.default_scale not in SUPPORTED_SCALES:
            raise ValueError("default_scale must be one of 0.50, 0.60, or 0.75")
        return self


class ArtManifestV3(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    schema_version: Literal[3]
    appearance_id: str = Field(min_length=1)
    assets: tuple[ArtAssetV3, ...] = Field(min_length=2)
    composition: CompositionV3
    expression_semantics: dict[str, str]
    fallback: dict[str, str] = Field(default_factory=dict)

    @model_validator(mode="after")
    def validate_contract(self) -> ArtManifestV3:
        ids = [asset.stable_id for asset in self.assets]
        if len(ids) != len(set(ids)):
            raise ValueError("duplicate stable_id")

        bodies = {
            asset.stable_id for asset in self.assets if asset.asset_type == "body"
        }
        expressions = {
            asset.stable_id
            for asset in self.assets
            if asset.asset_type == "expression"
        }
        if self.composition.body_id not in bodies:
            raise ValueError("body_id must reference a body asset")
        if self.composition.default_expression_id not in expressions:
            raise ValueError("default_expression_id must reference an expression asset")

        missing = set(CORE_EXPRESSION_SEMANTICS) - set(self.expression_semantics)
        if missing:
            raise ValueError(
                "missing core expression semantic including neutral: "
                + ", ".join(sorted(missing))
            )
        for semantic, stable_id in self.expression_semantics.items():
            if not semantic:
                raise ValueError("expression semantic cannot be empty")
            if stable_id not in expressions:
                raise ValueError(
                    f"unknown expression asset '{stable_id}' for semantic '{semantic}'"
                )

        known_semantics = set(self.expression_semantics)
        for semantic, target in self.fallback.items():
            if semantic not in known_semantics or target not in known_semantics:
                raise ValueError("fallback must reference declared expression semantics")
            visited: set[str] = set()
            current = semantic
            while current in self.fallback:
                if current in visited:
                    raise ValueError("fallback cycle")
                visited.add(current)
                current = self.fallback[current]
            if current != "neutral":
                raise ValueError("fallback chain must terminate at neutral")
        return self
