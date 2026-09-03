import hashlib
import json
import shutil
import subprocess
import zipfile
from pathlib import Path

import pytest


VERIFY_SCRIPT = Path(__file__).parents[2] / "scripts" / "verify-release.ps1"
REQUIRED_EXECUTABLES = ["FgoPet.App.exe", "FgoPet.AgentRelay.exe", "FgoPet.CodexAdapter.exe"]


def _write_candidate(root: Path, files: dict[str, bytes], manifest_files: list[dict] | None = None) -> None:
    archive = root / "app" / "FgoPet-win-x64-0.1.0.zip"
    archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, "w") as package:
        for path, data in files.items():
            package.writestr(path, data)
    entries = manifest_files or [
        {"path": path, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)}
        for path, data in sorted(files.items())
    ]
    (root / "manifest.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "runtime_identifier": "win-x64",
                "framework_dependent": True,
                "application_version": "0.1.0",
                "required_executables": REQUIRED_EXECUTABLES,
                "files": entries,
            }
        ),
        encoding="utf-8",
    )
    (root / "SHA256SUMS").write_text(
        f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  app/{archive.name}\n", encoding="ascii"
    )


def _verify(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["pwsh", "-NoProfile", "-File", str(VERIFY_SCRIPT), "-CandidateRoot", str(root)],
        text=True,
        capture_output=True,
        check=False,
    )


@pytest.fixture
def candidate(tmp_path: Path) -> Path:
    root = tmp_path / "candidate"
    _write_candidate(root, {name: b"binary" for name in REQUIRED_EXECUTABLES} | {"data/config.json": b"{}"})
    return root


def test_verifier_accepts_stable_posix_paths_and_lowercase_sha256(candidate: Path) -> None:
    result = _verify(candidate)
    assert result.returncode == 0, result.stderr


def test_verifier_rejects_duplicate_manifest_path(candidate: Path) -> None:
    manifest = json.loads((candidate / "manifest.json").read_text(encoding="utf-8"))
    manifest["files"].append(manifest["files"][0])
    (candidate / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    assert _verify(candidate).returncode != 0


@pytest.mark.parametrize("path", ["logs/app.log", "role.fgopetpack", "source/Program.cs"])
def test_verifier_rejects_forbidden_payload_files(candidate: Path, path: str) -> None:
    _write_candidate(candidate, {name: b"binary" for name in REQUIRED_EXECUTABLES} | {path: b"forbidden"})
    assert _verify(candidate).returncode != 0


def test_verifier_rejects_missing_or_uppercase_hash(candidate: Path) -> None:
    manifest = json.loads((candidate / "manifest.json").read_text(encoding="utf-8"))
    manifest["files"][0]["sha256"] = "A" * 64
    (candidate / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    assert _verify(candidate).returncode != 0


def test_verifier_rejects_role_package_inside_app_archive(candidate: Path) -> None:
    _write_candidate(candidate, {name: b"binary" for name in REQUIRED_EXECUTABLES} | {"roles/mash.fgopetpack": b"role"})
    assert _verify(candidate).returncode != 0
