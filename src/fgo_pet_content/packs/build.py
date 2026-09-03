from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from tempfile import NamedTemporaryFile
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

from ..cache import atomic_write
from .models import PackManifestV1, PackValidationReport
from .validate import validate_pack_project


@dataclass(frozen=True, slots=True)
class PackBuildResult:
    archive: Path
    checksum: Path
    qa_report: Path
    release_notes: Path


class PackBuildError(ValueError):
    def __init__(self, report: PackValidationReport) -> None:
        self.report = report
        super().__init__("role package project validation failed")


def build_pack(project_dir: Path, output_dir: Path) -> PackBuildResult:
    project = Path(project_dir)
    report = validate_pack_project(project)
    if report.status != "PASS" or report.manifest is None:
        raise PackBuildError(report)
    manifest = report.manifest

    output = Path(output_dir)
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"{manifest.package_id}-{manifest.package_version}.fgopetpack"
    temporary_path: Path | None = None
    try:
        with NamedTemporaryFile(
            dir=output,
            prefix=f".{archive.name}.",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)
        _write_archive(project, manifest, temporary_path)
        temporary_path.replace(archive)
        temporary_path = None
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)

    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    checksum = output / f"{archive.name}.sha256"
    atomic_write(checksum, f"{digest}  {archive.name}\n".encode("ascii"))

    qa_report = output / "qa-report.json"
    atomic_write(
        qa_report,
        report.model_dump_json(indent=2, exclude={"manifest"}).encode("utf-8"),
    )
    release_notes = output / "release-notes.md"
    atomic_write(
        release_notes,
        _release_notes(manifest).encode("utf-8"),
    )
    return PackBuildResult(
        archive=archive,
        checksum=checksum,
        qa_report=qa_report,
        release_notes=release_notes,
    )


def _write_archive(project: Path, manifest: PackManifestV1, destination: Path) -> None:
    members = {"package.json", *manifest.files}
    contents: dict[str, bytes] = {
        "package.json": _canonical_json(manifest.model_dump(mode="json")),
    }
    for relative in manifest.files:
        contents[relative] = (project / _native_path(relative)).read_bytes()

    with ZipFile(
        destination,
        mode="w",
        compression=ZIP_DEFLATED,
        compresslevel=9,
        strict_timestamps=False,
    ) as archive:
        for name in sorted(members):
            info = ZipInfo(filename=name, date_time=(2020, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (0o100644 & 0xFFFF) << 16
            info.compress_type = ZIP_DEFLATED
            archive.writestr(info, contents[name], compress_type=ZIP_DEFLATED, compresslevel=9)


def _release_notes(manifest: PackManifestV1) -> str:
    appearances = ", ".join(item.appearance_id for item in manifest.appearances)
    return (
        f"# {manifest.display_name} {manifest.package_version}\n\n"
        f"- package_id: `{manifest.package_id}`\n"
        f"- servant_id: `{manifest.servant_id}`\n"
        f"- min_app_version: `{manifest.min_app_version or 'none'}`\n"
        f"- appearances: {appearances}\n"
        "\nThis release contains declarative role-package data only.\n"
    )


def _canonical_json(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _native_path(relative: str) -> Path:
    return Path(*PurePosixPath(relative).parts)
