from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from .v3_models import ArtManifestV3, CORE_EXPRESSION_SEMANTICS


@dataclass(frozen=True, slots=True)
class PreviewArtifacts:
    contact_sheet: Path
    composites: tuple[Path, ...]


def write_preview_artifacts(
    bundle: Path,
    manifest: ArtManifestV3,
    output_dir: Path,
) -> PreviewArtifacts:
    """Write deterministic semantic composites for human review."""
    body_asset = next(
        asset for asset in manifest.assets if asset.stable_id == manifest.composition.body_id
    )
    body = _open(bundle / _relative_path(body_asset.path))
    offset = manifest.composition.overlay_offset
    overlay_width = manifest.composition.overlay_size.width
    overlay_height = manifest.composition.overlay_size.height
    if offset.x + overlay_width > body.width or offset.y + overlay_height > body.height:
        raise ValueError("expression overlay exceeds body bounds")

    output_dir.mkdir(parents=True, exist_ok=True)
    composites: list[Path] = []
    cell_width, cell_height = 320, 300
    sheet = Image.new("RGBA", (cell_width * 4, cell_height * 2), (235, 235, 235, 255))
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default()
    assets_by_id = {asset.stable_id: asset for asset in manifest.assets}
    for index, semantic in enumerate(CORE_EXPRESSION_SEMANTICS):
        expression_id = manifest.expression_semantics[semantic]
        expression_asset = assets_by_id[expression_id]
        expression = _open(bundle / _relative_path(expression_asset.path))
        if expression.size != (overlay_width, overlay_height):
            raise ValueError("expression dimensions do not match composition overlay")
        composite = body.copy()
        composite.alpha_composite(expression, (offset.x, offset.y))
        destination = output_dir / f"composite-{index + 1:02d}-{semantic}.png"
        _save_png(composite, destination)
        composites.append(destination)

        thumbnail = composite.copy()
        thumbnail.thumbnail((cell_width - 24, cell_height - 60), Image.Resampling.LANCZOS)
        x, y = (index % 4) * cell_width, (index // 4) * cell_height
        sheet.alpha_composite(
            thumbnail,
            (x + (cell_width - thumbnail.width) // 2, y + 8),
        )
        draw.text((x + 8, y + cell_height - 34), semantic, fill=(24, 24, 24), font=font)

    contact_sheet = output_dir / "contact-sheet.png"
    _save_png(sheet, contact_sheet)
    return PreviewArtifacts(contact_sheet=contact_sheet, composites=tuple(composites))


def _relative_path(value: str) -> Path:
    path = Path(value)
    if not value or path.is_absolute() or "\\" in value or ".." in path.parts:
        raise ValueError("asset path must be a safe relative POSIX path")
    return path


def _open(path: Path) -> Image.Image:
    with Image.open(path) as opened:
        return opened.convert("RGBA")


def _save_png(image: Image.Image, destination: Path) -> None:
    image.save(destination, format="PNG", optimize=False, compress_level=9)
