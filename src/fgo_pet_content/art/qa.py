from __future__ import annotations

import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from pydantic import BaseModel, ConfigDict

from ..cache import atomic_write
from .models import ArtManifest


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
    canvas = Image.new("RGBA", (cell_width * 4, cell_height * 8), (225, 225, 225, 255))
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
    destination = bundle / "contact-sheet.png"
    canvas.save(destination)
    return destination


def validate_art_bundle(bundle: Path) -> ArtQaReport:
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
