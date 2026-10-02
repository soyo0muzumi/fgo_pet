"""Build a local Mash role pack with a separate Live2D appearance and static fallback.

Only Cubism model data enters the .fgopetpack. The application owns SDK code.
Keep generated package projects and archives in ignored artifacts, not Git.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "content"))
from fgo_pet_content.packs.build import build_pack  # noqa: E402
from fgo_pet_content.packs.validate import validate_pack_project  # noqa: E402


def _digest(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def build_local_pack(static_pack: Path, staged_model: Path, project: Path, release: Path) -> Path:
    repository = Path(__file__).resolve().parents[2]
    static_pack, staged_model, project, release = (path.resolve() for path in (static_pack, staged_model, project, release))
    for output in (project, release):
        if output.is_relative_to(repository) and not output.is_relative_to(repository / "artifacts"):
            raise ValueError("Generated outputs inside the repository must stay in ignored artifacts/")
        if output.exists():
            raise ValueError(f"Output already exists: {output}")
    package = json.loads((static_pack / "package.json").read_text(encoding="utf-8"))
    if package["package_id"] != "official.mash" or package["schema_version"] != 1:
        raise ValueError("Expected the existing official.mash static role pack")
    if any(item["appearance_id"] == "live2d_casual" for item in package["appearances"]):
        raise ValueError("The source pack already declares live2d_casual")
    model_source = staged_model / "model"
    model_path = model_source / "mash_casual.model3.json"
    model = json.loads(model_path.read_text(encoding="utf-8"))
    if model.get("Version") != 3 or not model.get("FileReferences", {}).get("Motions"):
        raise ValueError("Staged model has no validated motion references")

    project.mkdir(parents=True)
    for relative in package["files"]:
        source = static_pack / Path(relative)
        target = project / Path(relative)
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"Missing or linked static pack file: {relative}")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    old_appearance = project / "appearances" / "casual"
    new_appearance = project / "appearances" / "live2d_casual"
    shutil.copytree(old_appearance, new_appearance)
    model_target = new_appearance / "live2d"
    shutil.copytree(model_source, model_target)
    manifest_path = new_appearance / "manifest.json"
    appearance = json.loads(manifest_path.read_text(encoding="utf-8"))
    appearance["appearance_id"] = "live2d_casual"
    appearance["live2d"] = {
        "model_path": "live2d/mash_casual.model3.json",
        "files": [
            {"path": path.relative_to(new_appearance).as_posix(), "sha256": _digest(path)}
            for path in sorted(model_target.rglob("*")) if path.is_file()
        ],
    }
    manifest_path.write_text(json.dumps(appearance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    package["package_version"] = "1.1.0"
    package["capabilities"] = sorted({*package["capabilities"], "portrait.live2d.v1"})
    package["appearances"].append({"appearance_id": "live2d_casual", "manifest_path": "appearances/live2d_casual/manifest.json"})
    package["files"] = sorted(path.relative_to(project).as_posix() for path in project.rglob("*") if path.is_file())
    (project / "package.json").write_text(json.dumps(package, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    report = validate_pack_project(project)
    if report.status != "PASS":
        raise ValueError("Pack validation failed: " + "; ".join(f"{issue.check_id}:{issue.path}" for issue in report.errors))
    result = build_pack(project, release)
    return result.archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--static-pack", required=True, type=Path)
    parser.add_argument("--staged-model", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--release", required=True, type=Path)
    args = parser.parse_args()
    print(build_local_pack(args.static_pack, args.staged_model, args.project, args.release))


if __name__ == "__main__":
    main()
