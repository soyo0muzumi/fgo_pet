from __future__ import annotations

from .models import ArtManifest
from .v3_models import (
    ArtAssetV3,
    ArtManifestV3,
    CompositionV3,
    CORE_EXPRESSION_SEMANTICS,
)


def migrate_v2_to_v3(
    manifest: ArtManifest,
    semantic_map: dict[str, str],
) -> ArtManifestV3:
    """Convert reviewed schema-v2 output without inferring emotional meaning."""
    body = next(
        (asset for asset in manifest.assets if asset.stable_id == manifest.composition.body_id),
        None,
    )
    if body is None:
        raise ValueError("composition body asset is missing")
    composition = manifest.composition
    if (
        composition.overlay_offset.x + composition.overlay_size.width
        > body.crop_rect.width
        or composition.overlay_offset.y + composition.overlay_size.height
        > body.crop_rect.height
    ):
        raise ValueError("expression overlay exceeds body bounds")

    ordered_semantics = {
        semantic: semantic_map[semantic]
        for semantic in CORE_EXPRESSION_SEMANTICS
        if semantic in semantic_map
    }
    ordered_semantics.update(
        (semantic, semantic_map[semantic])
        for semantic in sorted(set(semantic_map) - set(CORE_EXPRESSION_SEMANTICS))
    )

    assets = tuple(
        ArtAssetV3(
            type="body" if asset.stable_id == manifest.composition.body_id else "expression",
            stable_id=asset.stable_id,
            path=asset.runtime_path,
            sha256=_required_runtime_hash(asset.stable_id, asset.runtime_sha256),
        )
        for asset in manifest.assets
    )
    fallback = {
        semantic: "neutral"
        for semantic in ordered_semantics
        if semantic != "neutral"
    }
    return ArtManifestV3(
        schema_version=3,
        appearance_id=manifest.outfit_id,
        assets=assets,
        composition=CompositionV3.model_validate(composition.model_dump()),
        expression_semantics=ordered_semantics,
        fallback=fallback,
    )


def _required_runtime_hash(stable_id: str, value: str | None) -> str:
    if value is None:
        raise ValueError(f"asset '{stable_id}' has no runtime_sha256")
    return value
