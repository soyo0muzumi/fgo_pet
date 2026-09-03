from __future__ import annotations

import json
from pathlib import Path

from fgo_pet_content.packs.validate import validate_pack_project


def test_valid_project_passes_closed_file_and_art_validation(pack_project: Path) -> None:
    report = validate_pack_project(pack_project)

    assert report.status == "PASS"
    assert not report.errors
    assert report.declared_files == tuple(sorted(report.declared_files))


def test_validator_rejects_undeclared_files_and_forbidden_extensions(pack_project: Path) -> None:
    (pack_project / "notes.py").write_text("print('no')", encoding="utf-8")

    report = validate_pack_project(pack_project)

    assert report.status == "FAIL"
    assert {issue.check_id for issue in report.errors} >= {
        "file.undeclared",
        "file.extension",
    }


def test_validator_rejects_missing_preview_and_appearance_files(pack_project: Path) -> None:
    package_path = pack_project / "package.json"
    package = json.loads(package_path.read_text(encoding="utf-8"))
    package["preview_path"] = "previews/missing.png"
    package["appearances"][0]["manifest_path"] = "appearances/missing/manifest.json"
    package["files"] = [
        "previews/missing.png",
        "appearances/missing/manifest.json",
        *package["files"][2:],
    ]
    package_path.write_text(json.dumps(package), encoding="utf-8")

    report = validate_pack_project(pack_project)

    assert report.status == "FAIL"
    assert {issue.check_id for issue in report.errors} >= {
        "preview.missing",
        "appearance.manifest_missing",
    }


def test_validator_rejects_hash_mismatch_and_fallback_cycle(pack_project: Path) -> None:
    appearance_path = pack_project / "appearances" / "casual" / "manifest.json"
    appearance = json.loads(appearance_path.read_text(encoding="utf-8"))
    appearance["assets"][1]["sha256"] = "sha256:" + "0" * 64
    appearance_path.write_text(json.dumps(appearance), encoding="utf-8")

    report = validate_pack_project(pack_project)

    assert report.status == "FAIL"
    assert any(issue.check_id == "asset.hash" for issue in report.errors)

    appearance["fallback"]["neutral"] = "happy"
    appearance["fallback"]["happy"] = "neutral"
    appearance_path.write_text(json.dumps(appearance), encoding="utf-8")
    report = validate_pack_project(pack_project)

    assert report.status == "FAIL"
    assert any(issue.check_id == "appearance.manifest" for issue in report.errors)
