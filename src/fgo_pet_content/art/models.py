from __future__ import annotations

import re
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator


class Rect(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    left: int = Field(ge=0)
    top: int = Field(ge=0)
    right: int = Field(gt=0)
    bottom: int = Field(gt=0)

    @model_validator(mode="after")
    def validate_size(self) -> Rect:
        if self.right <= self.left or self.bottom <= self.top:
            raise ValueError("rectangle must have positive area")
        return self

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top


class Anchor(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    x: int = Field(ge=0)
    y: int = Field(ge=0)


class Point(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    x: int = Field(ge=0)
    y: int = Field(ge=0)


class Size(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    width: int = Field(gt=0)
    height: int = Field(gt=0)


class Composition(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    body_id: str
    default_expression_id: str
    overlay_offset: Point
    overlay_size: Size
    panel_anchor: Point
    default_scale: float = Field(gt=0, le=1)


class SourceImage(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    path: str
    sha256: str
    width: int = Field(gt=0)
    height: int = Field(gt=0)
    mode: str


class ArtAsset(BaseModel):
    model_config = ConfigDict(extra="forbid")

    stable_id: str
    semantic_label: str = Field(min_length=1)
    crop_rect: Rect
    anchor: Anchor
    raw_path: str
    runtime_path: str
    raw_sha256: str | None = None
    runtime_sha256: str | None = None
    foreground_bbox: Rect | None = None

    @model_validator(mode="after")
    def validate_identity_and_anchor(self) -> ArtAsset:
        if self.stable_id != "full_body" and not re.fullmatch(
            r"r0[1-7]c0[1-4]", self.stable_id
        ):
            raise ValueError("invalid stable art ID")
        if self.anchor.x > self.crop_rect.width or self.anchor.y > self.crop_rect.height:
            raise ValueError("anchor must be relative to and inside the crop")
        return self


class ArtManifest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schema_version: Literal[2]
    outfit_id: str = "mash_casual_98001000"
    source: SourceImage
    assets: list[ArtAsset]
    composition: Composition

    @model_validator(mode="after")
    def validate_complete_bundle(self) -> ArtManifest:
        expected = {"full_body"} | {
            f"r{row:02d}c{column:02d}"
            for row in range(1, 8)
            for column in range(1, 5)
        }
        ids = [asset.stable_id for asset in self.assets]
        if set(ids) != expected or len(ids) != len(expected):
            raise ValueError("manifest must contain one full body and the complete 7x4 grid")
        labels = [asset.semantic_label for asset in self.assets]
        if len(labels) != len(set(labels)):
            raise ValueError("semantic labels must be unique")
        for asset in self.assets:
            rect = asset.crop_rect
            if rect.right > self.source.width or rect.bottom > self.source.height:
                raise ValueError(f"crop {asset.stable_id} is outside source bounds")
        composition = self.composition
        if composition.body_id != "full_body":
            raise ValueError("composition body must be full_body")
        assets_by_id = {asset.stable_id: asset for asset in self.assets}
        if composition.default_expression_id not in expected - {"full_body"}:
            raise ValueError("default expression must reference an expression asset")
        body = assets_by_id[composition.body_id]
        expression = assets_by_id[composition.default_expression_id]
        if (
            expression.crop_rect.width != composition.overlay_size.width
            or expression.crop_rect.height != composition.overlay_size.height
        ):
            raise ValueError("overlay size must match expression crop dimensions")
        if (
            composition.overlay_offset.x + composition.overlay_size.width
            > body.crop_rect.width
            or composition.overlay_offset.y + composition.overlay_size.height
            > body.crop_rect.height
        ):
            raise ValueError("expression overlay exceeds body bounds")
        if (
            composition.panel_anchor.x >= body.crop_rect.width
            or composition.panel_anchor.y >= body.crop_rect.height
        ):
            raise ValueError("panel anchor must be inside body canvas")
        return self
