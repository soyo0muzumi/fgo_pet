from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest
from PIL import Image, ImageDraw


CORE_SEMANTICS = (
    "neutral",
    "happy",
    "excited",
    "shy",
    "concerned",
    "sad",
    "surprised",
    "angry",
)


def _sha256(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def _write_png(path: Path, size: tuple[int, int], color: tuple[int, int, int, int]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image = Image.new("RGBA", size, (255, 255, 255, 0))
    ImageDraw.Draw(image).rectangle(
        (2, 2, size[0] - 3, size[1] - 3),
        fill=color,
    )
    image.save(path, format="PNG")


def _write_appearance(project: Path, *, fallback: dict[str, str] | None = None) -> None:
    body = project / "appearances" / "casual" / "runtime" / "full_body.png"
    expression = (
        project
        / "appearances"
        / "casual"
        / "runtime"
        / "expressions"
        / "r01c01.png"
    )
    _write_png(body, (32, 32), (70, 90, 150, 255))
    _write_png(expression, (16, 16), (210, 120, 140, 255))
    semantics = {semantic: "r01c01" for semantic in CORE_SEMANTICS}
    manifest = {
        "schema_version": 3,
        "appearance_id": "casual",
        "assets": [
            {
                "type": "body",
                "stable_id": "full_body",
                "path": "runtime/full_body.png",
                "sha256": _sha256(body),
            },
            {
                "type": "expression",
                "stable_id": "r01c01",
                "path": "runtime/expressions/r01c01.png",
                "sha256": _sha256(expression),
            },
        ],
        "composition": {
            "body_id": "full_body",
            "default_expression_id": "r01c01",
            "overlay_offset": {"x": 0, "y": 0},
            "overlay_size": {"width": 16, "height": 16},
            "panel_anchor": {"x": 16, "y": 16},
            "default_scale": 0.5,
        },
        "expression_semantics": semantics,
        "fallback": fallback
        or {semantic: "neutral" for semantic in CORE_SEMANTICS if semantic != "neutral"},
    }
    manifest_path = project / "appearances" / "casual" / "manifest.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")


@pytest.fixture
def pack_project(tmp_path: Path) -> Path:
    project = tmp_path / "project"
    project.mkdir()
    _write_png(project / "previews" / "library.png", (24, 24), (100, 140, 190, 255))
    _write_appearance(project)
    package = {
        "schema_version": 1,
        "package_id": "official.mash",
        "package_version": "1.0.0",
        "servant_id": "mash_kyrielight",
        "display_name": "玛修·基列莱特",
        "publisher": "community",
        "min_app_version": "1.0.0",
        "capabilities": ["art.v3"],
        "preview_path": "previews/library.png",
        "appearances": [
            {
                "appearance_id": "casual",
                "manifest_path": "appearances/casual/manifest.json",
            }
        ],
        "files": [
            "previews/library.png",
            "appearances/casual/manifest.json",
            "appearances/casual/runtime/full_body.png",
            "appearances/casual/runtime/expressions/r01c01.png",
        ],
    }
    (project / "package.json").write_text(
        json.dumps(package, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return project
