import hashlib
import json
from pathlib import Path

from fgo_pet_content.art.v3_models import Live2DAppearanceV1
from fgo_pet_content.packs.validate import _validate_live2d


def _fixture(tmp_path: Path, texture: str = "texture.png") -> Live2DAppearanceV1:
    root = tmp_path / "appearances" / "casual" / "live2d"
    root.mkdir(parents=True)
    content = {
        "mash.model3.json": json.dumps({"Version": 3, "FileReferences": {
            "Moc": "mash.moc3", "Textures": [texture],
            "Motions": {"Idle": [{"File": "idle.motion3.json"}]},
        }}).encode(),
        "mash.moc3": b"MOC3",
        "texture.png": b"png",
        "idle.motion3.json": b"{}",
    }
    for name, data in content.items():
        (root / name).write_bytes(data)
    return Live2DAppearanceV1.model_validate({
        "model_path": "live2d/mash.model3.json",
        "files": [{
            "path": f"live2d/{name}",
            "sha256": "sha256:" + hashlib.sha256(data).hexdigest(),
        } for name, data in content.items()],
    })


def test_live2d_model_references_match_hashed_resources(tmp_path: Path) -> None:
    appearance = _fixture(tmp_path)
    issues = []

    _validate_live2d(tmp_path, "appearances/casual/manifest.json", appearance, issues)

    assert issues == []


def test_live2d_rejects_reference_traversal(tmp_path: Path) -> None:
    appearance = _fixture(tmp_path, "../outside.png")
    issues = []

    _validate_live2d(tmp_path, "appearances/casual/manifest.json", appearance, issues)

    assert issues[0].check_id == "live2d.reference_path"


def test_live2d_rejects_tampered_moc(tmp_path: Path) -> None:
    appearance = _fixture(tmp_path)
    (tmp_path / "appearances" / "casual" / "live2d" / "mash.moc3").write_bytes(b"changed")
    issues = []

    _validate_live2d(tmp_path, "appearances/casual/manifest.json", appearance, issues)

    assert issues[0].check_id == "live2d.hash"
