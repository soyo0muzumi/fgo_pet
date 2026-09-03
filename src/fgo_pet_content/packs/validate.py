from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
from typing import Iterable

from PIL import Image

from ..art.v3_models import ArtManifestV3
from .models import (
    PackManifestV1,
    PackValidationIssue,
    PackValidationReport,
    is_safe_relative_path,
)


MAX_ENTRIES = 1024
MAX_ENTRY_BYTES = 32 * 1024 * 1024
MAX_EXPANDED_BYTES = 512 * 1024 * 1024
ALLOWED_EXTENSIONS = frozenset({".png", ".jpg", ".jpeg", ".json", ".md", ".txt"})


def validate_pack_project(project_dir: Path) -> PackValidationReport:
    project = Path(project_dir)
    if not project.is_dir():
        return _report(
            [
                PackValidationIssue(
                    check_id="project.missing",
                    detail="project directory is missing",
                )
            ]
        )

    package_path = project / "package.json"
    try:
        payload = json.loads(package_path.read_text(encoding="utf-8"))
        manifest = PackManifestV1.model_validate(payload)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError):
        return _report(
            [
                PackValidationIssue(
                    check_id="package.manifest",
                    path="package.json",
                    detail="package.json does not satisfy the pack v1 contract",
                )
            ]
        )

    errors: list[PackValidationIssue] = []
    actual_files = _list_files(project, errors)
    if not manifest.files:
        errors.append(
            PackValidationIssue(
                check_id="package.files_missing",
                path="package.json",
                detail="new role-package projects must declare their included files",
            )
        )
    declared = {"package.json", *manifest.files}
    for relative in sorted(set(actual_files) - declared):
        errors.append(
            PackValidationIssue(
                check_id="file.undeclared",
                path=relative,
                detail="file is not declared by package.json",
            )
        )
    for relative in sorted(declared - set(actual_files)):
        errors.append(
            PackValidationIssue(
                check_id="file.missing",
                path=relative,
                detail="declared file is missing",
            )
        )

    total_bytes = 0
    for relative in sorted(actual_files):
        path = project / _native_path(relative)
        suffix = path.suffix.lower()
        if suffix not in ALLOWED_EXTENSIONS:
            errors.append(
                PackValidationIssue(
                    check_id="file.extension",
                    path=relative,
                    detail="file extension is not permitted in a role package",
                )
            )
        try:
            size = path.stat().st_size
        except OSError:
            continue
        total_bytes += size
        if size > MAX_ENTRY_BYTES:
            errors.append(
                PackValidationIssue(
                    check_id="file.entry_size",
                    path=relative,
                    detail="file exceeds the per-entry size limit",
                )
            )
    if len(actual_files) + 1 > MAX_ENTRIES:
        errors.append(
            PackValidationIssue(
                check_id="project.entry_count",
                detail="project exceeds the package entry-count limit",
            )
        )
    if total_bytes + _size_if_file(package_path) > MAX_EXPANDED_BYTES:
        errors.append(
            PackValidationIssue(
                check_id="project.expanded_size",
                detail="project exceeds the expanded-size limit",
            )
        )

    preview = _safe_project_path(project, manifest.preview_path)
    if preview is None or not preview.is_file():
        errors.append(
            PackValidationIssue(
                check_id="preview.missing",
                path=manifest.preview_path,
                detail="declared preview image is missing",
            )
        )
    elif not _readable_image(preview):
        errors.append(
            PackValidationIssue(
                check_id="preview.image",
                path=manifest.preview_path,
                detail="preview image is not readable",
            )
        )

    for appearance in manifest.appearances:
        _validate_appearance(project, appearance.manifest_path, appearance.appearance_id, errors)

    report = _report(errors, declared_files=tuple(sorted(manifest.files)), manifest=manifest)
    return report


def _validate_appearance(
    project: Path,
    manifest_path: str,
    expected_appearance_id: str,
    errors: list[PackValidationIssue],
) -> None:
    path = _safe_project_path(project, manifest_path)
    if path is None or not path.is_file():
        errors.append(
            PackValidationIssue(
                check_id="appearance.manifest_missing",
                path=manifest_path,
                detail="declared appearance manifest is missing",
            )
        )
        return
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        manifest = ArtManifestV3.model_validate(payload)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError):
        errors.append(
            PackValidationIssue(
                check_id="appearance.manifest",
                path=manifest_path,
                detail="appearance manifest does not satisfy the art v3 contract",
            )
        )
        return
    if manifest.appearance_id != expected_appearance_id:
        errors.append(
            PackValidationIssue(
                check_id="appearance.identity",
                path=manifest_path,
                detail="appearance_id does not match package.json",
            )
        )

    appearance_root = PurePosixPath(manifest_path).parent
    image_paths: dict[str, Path] = {}
    for asset in manifest.assets:
        relative = _join_relative(appearance_root, asset.path)
        if relative is None:
            errors.append(
                PackValidationIssue(
                    check_id="asset.path_safe",
                    path=manifest_path,
                    detail="appearance asset path is not safe",
                )
            )
            continue
        asset_path = _safe_project_path(project, relative)
        if asset_path is None or not asset_path.is_file():
            errors.append(
                PackValidationIssue(
                    check_id="asset.missing",
                    path=relative,
                    detail="declared appearance asset is missing",
                )
            )
            continue
        image_paths[asset.stable_id] = asset_path
        if asset.sha256 != _sha256(asset_path):
            errors.append(
                PackValidationIssue(
                    check_id="asset.hash",
                    path=relative,
                    detail="asset hash does not match the appearance manifest",
                )
            )
        try:
            with Image.open(asset_path) as opened:
                image = opened.convert("RGBA")
            if image.getchannel("A").getbbox() is None:
                errors.append(
                    PackValidationIssue(
                        check_id="asset.alpha",
                        path=relative,
                        detail="asset has no visible alpha",
                    )
                )
        except (OSError, ValueError):
            errors.append(
                PackValidationIssue(
                    check_id="asset.image",
                    path=relative,
                    detail="asset image is not readable",
                )
            )

    body_path = image_paths.get(manifest.composition.body_id)
    if body_path is None:
        return
    try:
        with Image.open(body_path) as opened:
            body_size = opened.size
    except (OSError, ValueError):
        return

    composition = manifest.composition
    if (
        composition.overlay_offset.x + composition.overlay_size.width > body_size[0]
        or composition.overlay_offset.y + composition.overlay_size.height > body_size[1]
    ):
        errors.append(
            PackValidationIssue(
                check_id="composition.bounds",
                path=manifest_path,
                detail="expression overlay exceeds body bounds",
            )
        )
    if composition.panel_anchor.x >= body_size[0] or composition.panel_anchor.y >= body_size[1]:
        errors.append(
            PackValidationIssue(
                check_id="composition.panel_anchor",
                path=manifest_path,
                detail="panel anchor is outside body bounds",
            )
        )

    expected_size = (composition.overlay_size.width, composition.overlay_size.height)
    for asset in manifest.assets:
        if asset.asset_type != "expression" or asset.stable_id not in image_paths:
            continue
        try:
            with Image.open(image_paths[asset.stable_id]) as opened:
                size = opened.size
        except (OSError, ValueError):
            continue
        if size != expected_size:
            errors.append(
                PackValidationIssue(
                    check_id="asset.overlay_dimensions",
                    path=asset.path,
                    detail="expression dimensions do not match composition overlay",
                )
            )


def _list_files(project: Path, errors: list[PackValidationIssue]) -> list[str]:
    result: list[str] = []
    for root, directories, filenames in os.walk(project, topdown=True, followlinks=False):
        root_path = Path(root)
        kept_directories: list[str] = []
        for name in directories:
            path = root_path / name
            if _is_link(path):
                errors.append(
                    PackValidationIssue(
                        check_id="file.symlink",
                        path=_relative(root_path, path, project),
                        detail="symbolic-link or reparse-point entries are not allowed",
                    )
                )
            else:
                kept_directories.append(name)
        directories[:] = kept_directories
        for name in filenames:
            path = root_path / name
            relative = _relative(root_path, path, project)
            if _is_link(path):
                errors.append(
                    PackValidationIssue(
                        check_id="file.symlink",
                        path=relative,
                        detail="symbolic-link or reparse-point entries are not allowed",
                    )
                )
            result.append(relative)
    return result


def _is_link(path: Path) -> bool:
    try:
        stat_result = path.lstat()
    except OSError:
        return False
    return path.is_symlink() or bool(getattr(stat_result, "st_file_attributes", 0) & 0x400)


def _relative(root: Path, path: Path, project: Path) -> str:
    return path.relative_to(project).as_posix()


def _native_path(relative: str) -> Path:
    return Path(*PurePosixPath(relative).parts)


def _safe_project_path(project: Path, relative: str) -> Path | None:
    if not is_safe_relative_path(relative):
        return None
    candidate = (project / _native_path(relative)).resolve()
    return candidate if candidate.is_relative_to(project.resolve()) else None


def _join_relative(base: PurePosixPath, child: str) -> str | None:
    if not is_safe_relative_path(child):
        return None
    combined = base / PurePosixPath(child)
    value = combined.as_posix()
    return value if is_safe_relative_path(value) else None


def _readable_image(path: Path) -> bool:
    try:
        with Image.open(path) as opened:
            opened.verify()
        return True
    except (OSError, ValueError):
        return False


def _sha256(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def _size_if_file(path: Path) -> int:
    try:
        return path.stat().st_size if path.is_file() else 0
    except OSError:
        return 0


def _report(
    errors: Iterable[PackValidationIssue],
    *,
    declared_files: tuple[str, ...] = (),
    manifest: PackManifestV1 | None = None,
) -> PackValidationReport:
    error_list = list(errors)
    return PackValidationReport(
        status="PASS" if not error_list else "FAIL",
        errors=error_list,
        warnings=[],
        declared_files=declared_files,
        manifest=manifest,
    )
