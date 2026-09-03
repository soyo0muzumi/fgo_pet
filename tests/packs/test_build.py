from __future__ import annotations

import json
import shutil
from pathlib import Path
from zipfile import ZipFile

import pytest

from fgo_pet_content.packs.build import PackBuildError, build_pack


def test_build_is_byte_for_byte_deterministic_and_contains_only_declared_data(
    pack_project: Path, tmp_path: Path
) -> None:
    first = build_pack(pack_project, tmp_path / "first")
    second = build_pack(pack_project, tmp_path / "second")

    assert first.archive.read_bytes() == second.archive.read_bytes()
    assert first.checksum.read_bytes() == second.checksum.read_bytes()
    assert first.qa_report.read_bytes() == second.qa_report.read_bytes()
    assert first.release_notes.read_bytes() == second.release_notes.read_bytes()
    with ZipFile(first.archive) as archive:
        names = archive.namelist()
        assert names == sorted(names)
        assert names == [
            "appearances/casual/manifest.json",
            "appearances/casual/runtime/expressions/r01c01.png",
            "appearances/casual/runtime/full_body.png",
            "package.json",
            "previews/library.png",
        ]
        assert all(Path(name).suffix.lower() in {".json", ".png"} for name in names)
        assert all("project" not in name for name in names)


def test_build_canonicalizes_manifest_sequence_order(pack_project: Path, tmp_path: Path) -> None:
    reordered = tmp_path / "reordered-project"
    shutil.copytree(pack_project, reordered)
    package_path = reordered / "package.json"
    package = json.loads(package_path.read_text(encoding="utf-8"))
    package["capabilities"] = list(reversed(package["capabilities"]))
    package["files"] = list(reversed(package["files"]))
    package_path.write_text(json.dumps(package), encoding="utf-8")

    first = build_pack(pack_project, tmp_path / "first")
    second = build_pack(reordered, tmp_path / "second")

    assert first.archive.read_bytes() == second.archive.read_bytes()


def test_build_refuses_invalid_project_without_writing_release_archive(
    pack_project: Path, tmp_path: Path
) -> None:
    (pack_project / "unlisted.txt").write_text("not declared", encoding="utf-8")

    with pytest.raises(PackBuildError) as caught:
        build_pack(pack_project, tmp_path / "release")

    assert caught.value.report.status == "FAIL"
    assert not (tmp_path / "release").exists()
