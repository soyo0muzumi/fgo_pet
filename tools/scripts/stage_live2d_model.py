"""Stage a local Cubism export for development without changing its source files.

The output is an ignored local build input, not an installed role package or a
verified renderer. The first sorted motion is provisionally Idle; the rest are
provisionally TapBody until their visual meaning has been reviewed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import tempfile
from pathlib import Path, PurePosixPath
from typing import Any


def _read_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path.name}")
    return value


def _source(root: Path, reference: str) -> tuple[Path, PurePosixPath]:
    if not isinstance(reference, str) or not reference or "\\" in reference:
        raise ValueError(f"Expected a relative POSIX asset path: {reference!r}")
    relative = PurePosixPath(reference)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        raise ValueError(f"Expected a relative POSIX asset path: {reference!r}")
    candidate = root.joinpath(*relative.parts).resolve()
    if not candidate.is_relative_to(root.resolve()) or not candidate.is_file():
        raise ValueError(f"Missing or escaping asset: {reference!r}")
    if candidate.stat().st_size == 0 or candidate.stat().st_size > 100 * 1024 * 1024:
        raise ValueError(f"Invalid asset size: {reference!r}")
    return candidate, relative


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def stage_model(model: Path, motions: Path, sdk: Path, output: Path, *, desktop_behaviors: bool = False) -> dict[str, Any]:
    model, motions, sdk, output = (path.resolve() for path in (model, motions, sdk, output))
    repository = Path(__file__).resolve().parents[2]
    if output.is_relative_to(repository) and not output.is_relative_to(repository / "artifacts"):
        raise ValueError("Repository output must stay under ignored artifacts/")
    if output.exists():
        raise ValueError(f"Output already exists: {output}")
    if not model.is_file() or model.suffixes[-2:] != [".model3", ".json"]:
        raise ValueError("Model must be an existing .model3.json file")
    manifest = _read_json(model)
    references = manifest.get("FileReferences")
    if manifest.get("Version") != 3 or not isinstance(references, dict):
        raise ValueError("Unsupported model3 manifest")
    if references.get("Motions"):
        raise ValueError("Model already declares motions; preserve its existing mapping")
    texture_refs = references.get("Textures")
    if not isinstance(texture_refs, list) or not texture_refs:
        raise ValueError("Model has no textures")
    names = [references.get("Moc"), *texture_refs]
    names += [references[key] for key in ("Physics", "DisplayInfo") if references.get(key)]
    sources = [_source(model.parent, name) for name in names]
    if sources[0][0].read_bytes()[:4] != b"MOC3":
        raise ValueError("Moc file has no MOC3 signature")
    for texture in sources[1:1 + len(texture_refs)]:
        if texture[0].read_bytes()[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError(f"Texture is not PNG: {texture[1]}")

    display_ref = references.get("DisplayInfo")
    if not display_ref:
        raise ValueError("DisplayInfo is required to validate motion parameters")
    display = _read_json(_source(model.parent, display_ref)[0])
    parameters = display.get("Parameters")
    if not isinstance(parameters, list):
        raise ValueError("DisplayInfo has no parameter list")
    known_ids = {item["Id"] for item in parameters if isinstance(item, dict)
                 and isinstance(item.get("Id"), str) and item["Id"]}
    if not known_ids:
        raise ValueError("DisplayInfo has no usable parameter IDs")
    motion_files = sorted(motions.glob("*.motion3.json")) if motions.is_dir() else []
    if not motion_files:
        raise ValueError("No motion3 files found")
    for motion in motion_files:
        if not motion.resolve().is_relative_to(motions) or not motion.is_file():
            raise ValueError(f"Escaping motion file: {motion.name}")
        if motion.stat().st_size == 0 or motion.stat().st_size > 10 * 1024 * 1024:
            raise ValueError(f"Invalid motion size: {motion.name}")
        data = _read_json(motion)
        if data.get("Version") != 3 or not isinstance(data.get("Curves"), list):
            raise ValueError(f"Invalid motion3 file: {motion.name}")
        unknown = {curve.get("Id") for curve in data["Curves"]
                   if isinstance(curve, dict) and curve.get("Target") == "Parameter"} - known_ids
        if unknown:
            raise ValueError(f"Unknown motion parameter in {motion.name}: {sorted(unknown)}")

    core, _ = _source(sdk, "Core/live2dcubismcore.min.js")
    core_license, _ = _source(sdk, "Core/LICENSE.md")
    framework_license, _ = _source(sdk, "Framework/LICENSE.md")
    motion_entries = [{"File": f"motions/{motion.name}"} for motion in motion_files]
    references["Motions"] = {"Idle": motion_entries[:1], "TapBody": motion_entries[1:]}
    if desktop_behaviors:
        selected = {
            "Idle": ["hiyori_m01"],
            "TapBody": ["hiyori_m07", "hiyori_m08", "hiyori_m09"],
            "Thinking": ["thinking"], "Shy": ["shame"],
            "Concerned": ["hiyori_m10"], "Surprised": ["hiyori_m07"], "Sleep": ["tired"],
        }
        available = {path.name for path in motion_files}
        for names in selected.values():
            for name in names:
                if f"{name}.motion3.json" not in available:
                    raise ValueError(f"Missing desktop motion: {name}")
        references["Motions"] = {
            group: [{"File": f"motions/{name}.motion3.json"} for name in names]
            for group, names in selected.items()
        }
        used = {f"{name}.motion3.json" for names in selected.values() for name in names}
        motion_files = [motion for motion in motion_files if motion.name in used]
    provenance = {
        "modelSha256": _sha256(model),
        "coreSha256": _sha256(core),
        "motionCount": len(motion_files),
        "motionMapping": "reviewed desktop state groups" if desktop_behaviors else "provisional: first sorted motion Idle, remaining TapBody",
        "purpose": "local staging input; not an installed role package or renderer acceptance",
    }

    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="live2d-stage-", dir=output.parent) as temporary:
        staged = Path(temporary) / output.name
        model_dir = staged / "model"
        for source, relative in sources:
            target = model_dir.joinpath(*relative.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        motion_dir = model_dir / "motions"
        motion_dir.mkdir(parents=True)
        for motion in motion_files:
            shutil.copy2(motion, motion_dir / motion.name)
        (model_dir / model.name).write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        runtime = staged / "runtime"
        runtime.mkdir()
        shutil.copy2(core, runtime / core.name)
        shutil.copy2(core_license, runtime / "CORE-LICENSE.md")
        shutil.copy2(framework_license, runtime / "FRAMEWORK-LICENSE.md")
        (staged / "provenance.json").write_text(
            json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        os.replace(staged, output)
    return provenance


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--motions", required=True, type=Path)
    parser.add_argument("--sdk", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--desktop-behaviors", action="store_true", help="Use the reviewed Mash desktop state mapping")
    args = parser.parse_args()
    print(json.dumps(stage_model(args.model, args.motions, args.sdk, args.output, desktop_behaviors=args.desktop_behaviors), indent=2))


if __name__ == "__main__":
    main()
