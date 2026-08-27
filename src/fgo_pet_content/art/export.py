from __future__ import annotations

import hashlib
import json
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


def _hash(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def _save_png(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    image.save(temporary, format="PNG")
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
            overlay_offset=Point(x=24, y=0),
            overlay_size=Size(
                width=layout.expressions["r01c01"].width,
                height=layout.expressions["r01c01"].height,
            ),
            panel_anchor=Point(x=151, y=360),
            default_scale=0.60,
        ),
    )
    atomic_write(
        output_dir / "manifest.json",
        manifest.model_dump_json(indent=2).encode("utf-8"),
    )
    return manifest
