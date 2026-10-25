import json
from pathlib import Path

import pytest

from tools.scripts.stage_live2d_model import stage_model


def fixture_assets(root: Path) -> tuple[Path, Path, Path]:
    model = root / "mash.model3.json"
    (root / "mash.moc3").write_bytes(b"MOC3" + bytes(12))
    (root / "mash.4096").mkdir()
    (root / "mash.4096" / "texture_00.png").write_bytes(b"\x89PNG\r\n\x1a\n" + bytes(16))
    (root / "mash.cdi3.json").write_text(
        json.dumps({"Parameters": [{"Id": "ParamAngleX"}]}), encoding="utf-8"
    )
    model.write_text(json.dumps({"Version": 3, "FileReferences": {
        "Moc": "mash.moc3", "Textures": ["mash.4096/texture_00.png"],
        "DisplayInfo": "mash.cdi3.json",
    }}), encoding="utf-8")
    motions = root / "motions"
    motions.mkdir()
    for name in ("idle.motion3.json", "tap.motion3.json"):
        (motions / name).write_text(json.dumps({
            "Version": 3, "Curves": [{"Target": "Parameter", "Id": "ParamAngleX"}],
        }), encoding="utf-8")
    sdk = root / "sdk" / "Core"
    sdk.mkdir(parents=True)
    (sdk / "live2dcubismcore.min.js").write_text("core", encoding="utf-8")
    (sdk / "LICENSE.md").write_text("license", encoding="utf-8")
    (sdk.parent / "Framework").mkdir()
    (sdk.parent / "Framework" / "LICENSE.md").write_text("license", encoding="utf-8")
    return model, motions, sdk.parent


def test_stages_validated_model_with_motions_and_core(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    output = tmp_path / "output"

    result = stage_model(model, motions, sdk, output)

    manifest = json.loads((output / "model" / "mash.model3.json").read_text(encoding="utf-8"))
    assert [item["File"] for item in manifest["FileReferences"]["Motions"]["Idle"]] == ["motions/idle.motion3.json"]
    assert [item["File"] for item in manifest["FileReferences"]["Motions"]["TapBody"]] == ["motions/tap.motion3.json"]
    assert (output / "model" / "mash.moc3").read_bytes() == b"MOC3" + bytes(12)
    assert (output / "runtime" / "live2dcubismcore.min.js").exists()
    assert result["motionCount"] == 2


def test_rejects_path_traversal_before_creating_output(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    data = json.loads(model.read_text(encoding="utf-8"))
    data["FileReferences"]["Moc"] = "../outside.moc3"
    model.write_text(json.dumps(data), encoding="utf-8")
    output = tmp_path / "output"

    with pytest.raises(ValueError, match="relative"):
        stage_model(model, motions, sdk, output)
    assert not output.exists()


def test_rejects_motion_with_unknown_parameter(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    motion = motions / "tap.motion3.json"
    data = json.loads(motion.read_text(encoding="utf-8"))
    data["Curves"][0]["Id"] = "Unknown"
    motion.write_text(json.dumps(data), encoding="utf-8")

    with pytest.raises(ValueError, match="Unknown"):
        stage_model(model, motions, sdk, tmp_path / "output")


def test_rejects_copyrighted_asset_output_in_tracked_source(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    repository = Path(__file__).resolve().parents[1]
    output = repository / "plugins" / "must-not-stage-model-here"

    with pytest.raises(ValueError, match="ignored artifacts"):
        stage_model(model, motions, sdk, output)
    assert not output.exists()


def test_desktop_mapping_keeps_state_motions_out_of_random_taps(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    data = (motions / "tap.motion3.json").read_text()
    for name in [*(f"hiyori_m{i:02}" for i in range(1, 11)), "thinking", "shame", "tired"]:
        (motions / f"{name}.motion3.json").write_text(data)
    output = tmp_path / "output"
    stage_model(model, motions, sdk, output, desktop_behaviors=True)
    groups = json.loads((output / "model/mash.model3.json").read_text())["FileReferences"]["Motions"]
    assert groups["Thinking"] == [{"File": "motions/thinking.motion3.json"}]
    assert groups["Shy"] == [{"File": "motions/shame.motion3.json"}]
    assert groups["Sleep"] == [{"File": "motions/tired.motion3.json"}]
    assert groups["Concerned"] == [{"File": "motions/hiyori_m10.motion3.json"}]
    assert groups["TapBody"] == [{"File": f"motions/hiyori_m{i:02}.motion3.json"} for i in (7, 8, 9)]
    assert not (output / "model/motions/hiyori_m02.motion3.json").exists()


def test_desktop_mapping_rejects_missing_actions_before_output(tmp_path: Path):
    model, motions, sdk = fixture_assets(tmp_path)
    output = tmp_path / "output"
    with pytest.raises(ValueError, match="Missing desktop motion"):
        stage_model(model, motions, sdk, output, desktop_behaviors=True)
    assert not output.exists()
