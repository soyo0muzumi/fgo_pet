from __future__ import annotations

import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from pydantic import BaseModel, ConfigDict

from ..cache import atomic_write
from .background import has_meaningful_transparency
from .models import ArtManifest
from .preview import write_preview_artifacts
from .v3_models import ArtManifestV3


class ArtCheck(BaseModel):
    model_config = ConfigDict(extra="forbid")

    check_id: str
    asset_id: str | None = None
    detail: str


class ArtQaReport(BaseModel):
    model_config = ConfigDict(extra="forbid")

    status: str
    errors: list[ArtCheck]
    warnings: list[ArtCheck]


def _hash(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def _font(size: int):
    windows_font = Path("C:/Windows/Fonts/msyh.ttc")
    return ImageFont.truetype(str(windows_font), size) if windows_font.exists() else ImageFont.load_default()


def write_contact_sheet(bundle: Path, manifest: ArtManifest) -> Path:
    cell_width, cell_height = 240, 230
    canvas = Image.new("RGBA", (cell_width * 4, cell_height * 9), (225, 225, 225, 255))
    draw = ImageDraw.Draw(canvas)
    font = _font(15)
    for index, asset in enumerate(manifest.assets):
        column, row = index % 4, index // 4
        x, y = column * cell_width, row * cell_height
        path = bundle / asset.runtime_path
        with Image.open(path) as opened:
            thumbnail = opened.convert("RGBA")
        thumbnail.thumbnail((210, 190), Image.Resampling.LANCZOS)
        checker = Image.new("RGBA", thumbnail.size, (245, 245, 245, 255))
        checker.alpha_composite(thumbnail)
        canvas.alpha_composite(
            checker,
            (x + (cell_width - thumbnail.width) // 2, y + 5),
        )
        draw.text((x + 6, y + 198), f"{asset.stable_id} {asset.semantic_label}", fill=(20, 20, 20), font=font)
    assets_by_id = {asset.stable_id: asset for asset in manifest.assets}
    body_asset = assets_by_id[manifest.composition.body_id]
    with Image.open(bundle / body_asset.runtime_path) as opened:
        body = opened.convert("RGBA")
    for column, stable_id in enumerate(("r01c01", "r02c02", "r04c04", "r07c03")):
        expression_asset = assets_by_id[stable_id]
        with Image.open(bundle / expression_asset.runtime_path) as opened:
            expression = opened.convert("RGBA")
        composite = body.copy()
        offset = manifest.composition.overlay_offset
        composite.alpha_composite(expression, (offset.x, offset.y))
        composite.thumbnail((210, 190), Image.Resampling.LANCZOS)
        x, y = column * cell_width, cell_height * 8
        checker = Image.new("RGBA", composite.size, (245, 245, 245, 255))
        checker.alpha_composite(composite)
        canvas.alpha_composite(
            checker,
            (x + (cell_width - composite.width) // 2, y + 5),
        )
        draw.text(
            (x + 6, y + 198),
            f"composite {stable_id} @ ({offset.x},{offset.y})",
            fill=(20, 20, 20),
            font=font,
        )
    destination = bundle / "contact-sheet.png"
    canvas.save(destination)
    return destination


def validate_art_bundle(bundle: Path) -> ArtQaReport:
    try:
        payload = json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))
    except Exception:
        payload = None
    if isinstance(payload, dict) and payload.get("schema_version") == 3:
        return _validate_v3_bundle(bundle, payload)

    errors: list[ArtCheck] = []
    warnings: list[ArtCheck] = []
    manifest_path = bundle / "manifest.json"
    try:
        manifest = ArtManifest.model_validate_json(
            manifest_path.read_text(encoding="utf-8")
        )
    except Exception as error:
        return ArtQaReport(
            status="FAIL",
            errors=[ArtCheck(check_id="manifest.valid", detail=str(error))],
            warnings=[],
        )
    for asset in manifest.assets:
        raw_path = bundle / asset.raw_path
        runtime_path = bundle / asset.runtime_path
        for kind, relative, expected in (
            ("raw", asset.raw_path, asset.raw_sha256),
            ("runtime", asset.runtime_path, asset.runtime_sha256),
        ):
            path = bundle / relative
            if not path.exists():
                errors.append(
                    ArtCheck(
                        check_id=f"asset.{kind}_exists",
                        asset_id=asset.stable_id,
                        detail=str(path),
                    )
                )
            elif expected != _hash(path):
                errors.append(
                    ArtCheck(
                        check_id=f"asset.{kind}_hash",
                        asset_id=asset.stable_id,
                        detail="hash does not match manifest",
                    )
                )
        if raw_path.exists() and runtime_path.exists():
            try:
                with Image.open(raw_path) as raw_opened:
                    raw = raw_opened.convert("RGBA")
                with Image.open(runtime_path) as runtime_opened:
                    runtime = runtime_opened.convert("RGBA")
                if asset.stable_id != manifest.composition.body_id and runtime.size != (
                    manifest.composition.overlay_size.width,
                    manifest.composition.overlay_size.height,
                ):
                    errors.append(
                        ArtCheck(
                            check_id="asset.overlay_dimensions",
                            asset_id=asset.stable_id,
                            detail=(
                                f"runtime {runtime.size} != overlay "
                                f"{(manifest.composition.overlay_size.width, manifest.composition.overlay_size.height)}"
                            ),
                        )
                    )
                if raw.size != runtime.size:
                    errors.append(
                        ArtCheck(
                            check_id="asset.runtime_dimensions",
                            asset_id=asset.stable_id,
                            detail=f"raw {raw.size} != runtime {runtime.size}",
                        )
                    )
                elif has_meaningful_transparency(raw) and any(
                    runtime_alpha < raw_alpha
                    for raw_alpha, runtime_alpha in zip(
                        raw.getchannel("A").tobytes(),
                        runtime.getchannel("A").tobytes(),
                    )
                ):
                    errors.append(
                        ArtCheck(
                            check_id="asset.runtime_alpha_loss",
                            asset_id=asset.stable_id,
                            detail="runtime alpha is lower than raw alpha",
                        )
                    )
            except OSError as error:
                errors.append(
                    ArtCheck(
                        check_id="asset.image_readable",
                        asset_id=asset.stable_id,
                        detail=str(error),
                    )
                )
        if asset.foreground_bbox is None:
            errors.append(
                ArtCheck(
                    check_id="asset.foreground_nonempty",
                    asset_id=asset.stable_id,
                    detail="runtime crop has no opaque foreground",
                )
            )
        else:
            box = asset.foreground_bbox
            if box.left == 0 or box.top == 0 or box.right == asset.crop_rect.width or box.bottom == asset.crop_rect.height:
                warnings.append(
                    ArtCheck(
                        check_id="asset.foreground_touches_edge",
                        asset_id=asset.stable_id,
                        detail="review possible clipping or residual background",
                    )
                )
    if not errors:
        write_contact_sheet(bundle, manifest)
    report = ArtQaReport(
        status="PASS" if not errors else "FAIL",
        errors=errors,
        warnings=warnings,
    )
    atomic_write(
        bundle / "qa-report.json",
        report.model_dump_json(indent=2).encode("utf-8"),
    )
    return report


def _validate_v3_bundle(bundle: Path, payload: dict) -> ArtQaReport:
    errors: list[ArtCheck] = []
    warnings: list[ArtCheck] = []
    try:
        manifest = ArtManifestV3.model_validate(payload)
    except Exception:
        return _write_v3_report(
            bundle,
            ArtQaReport(
                status="FAIL",
                errors=[
                    ArtCheck(
                        check_id="manifest.valid",
                        detail="manifest does not satisfy the v3 contract",
                    )
                ],
                warnings=[],
            ),
        )

    root = bundle.resolve()
    files: dict[str, Path] = {}
    for asset in manifest.assets:
        path = _resolve_v3_asset(root, asset.path)
        if path is None:
            errors.append(ArtCheck(check_id="asset.path_safe", asset_id=asset.stable_id, detail="asset path is not safe"))
            continue
        files[asset.stable_id] = path
        if not path.is_file():
            errors.append(ArtCheck(check_id="asset.exists", asset_id=asset.stable_id, detail="asset file is missing"))
            continue
        if asset.sha256 != _hash(path):
            errors.append(ArtCheck(check_id="asset.hash", asset_id=asset.stable_id, detail="hash does not match manifest"))
        try:
            with Image.open(path) as opened:
                image = opened.convert("RGBA")
            if image.getchannel("A").getbbox() is None:
                errors.append(ArtCheck(check_id="asset.visible_alpha", asset_id=asset.stable_id, detail="asset has no visible alpha"))
            else:
                box = _visible_alpha_bbox(image)
                if box and (box[0] == 0 or box[1] == 0 or box[2] == image.width or box[3] == image.height):
                    errors.append(ArtCheck(check_id="asset.foreground_touches_edge", asset_id=asset.stable_id, detail="foreground touches the crop edge"))
        except OSError:
            errors.append(
                ArtCheck(
                    check_id="asset.image_readable",
                    asset_id=asset.stable_id,
                    detail="asset image is not readable",
                )
            )

    body = files.get(manifest.composition.body_id)
    if body is not None and body.is_file():
        try:
            with Image.open(body) as opened:
                body_size = opened.size
            composition = manifest.composition
            if (
                composition.overlay_offset.x + composition.overlay_size.width > body_size[0]
                or composition.overlay_offset.y + composition.overlay_size.height > body_size[1]
            ):
                errors.append(ArtCheck(check_id="composition.bounds", detail="expression overlay exceeds body bounds"))
            if composition.panel_anchor.x >= body_size[0] or composition.panel_anchor.y >= body_size[1]:
                errors.append(ArtCheck(check_id="composition.panel_anchor", detail="panel anchor is outside body bounds"))
        except OSError:
            errors.append(
                ArtCheck(
                    check_id="body.image_readable",
                    detail="body image is not readable",
                )
            )

    for asset in manifest.assets:
        if asset.asset_type != "expression" or asset.stable_id not in files:
            continue
        try:
            with Image.open(files[asset.stable_id]) as opened:
                size = opened.size
            expected = (manifest.composition.overlay_size.width, manifest.composition.overlay_size.height)
            if size != expected:
                errors.append(ArtCheck(check_id="asset.overlay_dimensions", asset_id=asset.stable_id, detail="expression dimensions do not match overlay"))
        except OSError:
            pass

    if not errors:
        try:
            write_preview_artifacts(bundle, manifest, bundle / "previews")
        except (OSError, ValueError):
            errors.append(
                ArtCheck(
                    check_id="preview.generated",
                    detail="preview artifacts could not be generated",
                )
            )
    return _write_v3_report(
        bundle,
        ArtQaReport(status="PASS" if not errors else "FAIL", errors=errors, warnings=warnings),
    )


def _resolve_v3_asset(root: Path, relative: str) -> Path | None:
    path = Path(relative)
    if not relative or path.is_absolute() or "\\" in relative or ".." in path.parts:
        return None
    candidate = (root / path).resolve()
    return candidate if candidate.is_relative_to(root) else None


def _visible_alpha_bbox(image: Image.Image):
    alpha = image.getchannel("A").point(lambda value: 255 if value >= 32 else 0)
    return alpha.getbbox()


def _write_v3_report(bundle: Path, report: ArtQaReport) -> ArtQaReport:
    atomic_write(bundle / "qa-report.json", report.model_dump_json(indent=2).encode("utf-8"))
    return report
