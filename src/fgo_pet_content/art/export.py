from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

from PIL import Image

from ..cache import atomic_write
from .background import remove_edge_background
from .models import (
    Anchor,
    ArtAsset,
    ArtManifest,
    Composition,
    Point,
    Rect,
    Size,
    SourceImage,
)
from .sheet import analyze_sheet
from .v3_models import ArtAssetV3, ArtManifestV3, CompositionV3, CORE_EXPRESSION_SEMANTICS
from .layout_spec import LayoutSpec


@dataclass(frozen=True, slots=True)
class AppearanceExportMetadata:
    appearance_id: str
    expression_semantics: dict[str, str]
    fallback: dict[str, str]
    overlay_offset: Point
    panel_anchor: Point
    default_scale: float
    default_expression_id: str | None = None


def _hash(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def _save_png(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    image.save(temporary, format="PNG", optimize=False, compress_level=9)
    temporary.replace(destination)


def _bbox(image: Image.Image) -> Rect | None:
    bounds = image.getchannel("A").getbbox()
    return Rect(left=bounds[0], top=bounds[1], right=bounds[2], bottom=bounds[3]) if bounds else None


def export_art_bundle(
    source_path: Path,
    output_dir: Path,
    labels: dict[str, str],
    *,
    tolerance: int = 32,
    feather: int = 2,
) -> ArtManifest:
    with Image.open(source_path) as opened:
        source = opened.convert("RGBA")
    layout = analyze_sheet(source)
    rectangles = {"full_body": layout.full_body, **layout.expressions}
    missing = set(layout.expressions) - set(labels)
    extra = set(labels) - set(layout.expressions)
    if missing or extra:
        raise ValueError(f"expression label mismatch: missing={sorted(missing)}, extra={sorted(extra)}")

    assets: list[ArtAsset] = []
    for stable_id, rect in rectangles.items():
        relative = (
            Path("full_body.png")
            if stable_id == "full_body"
            else Path("expressions") / f"{stable_id}.png"
        )
        raw_path = output_dir / "raw" / relative
        runtime_path = output_dir / "runtime" / relative
        raw = source.crop((rect.left, rect.top, rect.right, rect.bottom))
        runtime = remove_edge_background(raw, tolerance=tolerance, feather=feather)
        _save_png(raw, raw_path)
        _save_png(runtime, runtime_path)
        foreground = _bbox(runtime)
        anchor = Anchor(
            x=(foreground.left + foreground.right) // 2 if foreground else rect.width // 2,
            y=foreground.bottom if foreground else rect.height,
        )
        assets.append(
            ArtAsset(
                stable_id=stable_id,
                semantic_label="常服全身" if stable_id == "full_body" else labels[stable_id],
                crop_rect=rect,
                anchor=anchor,
                raw_path=str(raw_path.relative_to(output_dir)).replace("\\", "/"),
                runtime_path=str(runtime_path.relative_to(output_dir)).replace("\\", "/"),
                raw_sha256=_hash(raw_path),
                runtime_sha256=_hash(runtime_path),
                foreground_bbox=foreground,
            )
        )
    manifest = ArtManifest(
        schema_version=2,
        source=SourceImage(
            path=str(source_path),
            sha256=_hash(source_path),
            width=source.width,
            height=source.height,
            mode=source.mode,
        ),
        assets=assets,
        composition=Composition(
            body_id="full_body",
            default_expression_id="r01c01",
            overlay_offset=Point(x=13, y=0),
            overlay_size=Size(
                width=layout.expressions["r01c01"].width,
                height=layout.expressions["r01c01"].height,
            ),
            panel_anchor=Point(x=151, y=360),
            default_scale=0.50,
        ),
    )
    atomic_write(
        output_dir / "manifest.json",
        manifest.model_dump_json(indent=2).encode("utf-8"),
    )
    return manifest


def export_appearance_v3(
    source_path: Path,
    layout: LayoutSpec,
    metadata: AppearanceExportMetadata,
    output_dir: Path,
) -> ArtManifestV3:
    """Export a confirmed layout as a path-relative, runtime-only art v3 bundle."""
    with Image.open(source_path) as opened:
        source = opened.convert("RGBA")
    if source.size != (layout.source_size.width, layout.source_size.height):
        raise ValueError("layout source size does not match input image")

    expression_ids = [item.stable_id for item in layout.expressions]
    if set(metadata.expression_semantics) < set(CORE_EXPRESSION_SEMANTICS):
        raise ValueError("expression semantic mapping is missing a core semantic")

    output_dir.mkdir(parents=True, exist_ok=True)
    assets: list[ArtAssetV3] = []

    body = source.crop(
        (
            layout.full_body.left,
            layout.full_body.top,
            layout.full_body.right,
            layout.full_body.bottom,
        )
    )
    body = remove_edge_background(body)
    body_path = output_dir / "runtime" / "full_body.png"
    _save_png(body, body_path)
    assets.append(
        ArtAssetV3(
            type="body",
            stable_id="full_body",
            path="runtime/full_body.png",
            sha256=_hash(body_path),
        )
    )

    expression_size: Size | None = None
    for item in layout.expressions:
        expression = source.crop(
            (item.rect.left, item.rect.top, item.rect.right, item.rect.bottom)
        )
        expression = remove_edge_background(expression)
        if expression_size is None:
            expression_size = Size(width=expression.width, height=expression.height)
        elif expression.size != (expression_size.width, expression_size.height):
            raise ValueError("expression crops must have matching dimensions")
        expression_path = output_dir / "runtime" / "expressions" / f"{item.stable_id}.png"
        _save_png(expression, expression_path)
        assets.append(
            ArtAssetV3(
                type="expression",
                stable_id=item.stable_id,
                path=f"runtime/expressions/{item.stable_id}.png",
                sha256=_hash(expression_path),
            )
        )

    if expression_size is None or not expression_ids:
        raise ValueError("confirmed layout must contain expression assets")
    default_expression_id = metadata.default_expression_id or expression_ids[0]
    manifest = ArtManifestV3(
        schema_version=3,
        appearance_id=metadata.appearance_id,
        assets=tuple(assets),
        composition=CompositionV3(
            body_id="full_body",
            default_expression_id=default_expression_id,
            overlay_offset=metadata.overlay_offset,
            overlay_size=expression_size,
            panel_anchor=metadata.panel_anchor,
            default_scale=metadata.default_scale,
        ),
        expression_semantics=dict(metadata.expression_semantics),
        fallback=dict(metadata.fallback),
    )
    atomic_write(
        output_dir / "manifest.json",
        manifest.model_dump_json(indent=2, by_alias=True).encode("utf-8"),
    )
    return manifest
