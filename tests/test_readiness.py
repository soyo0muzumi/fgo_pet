import hashlib
import json
from pathlib import Path

from fgo_pet_content.readiness import ReadinessInputs, evaluate_readiness


def _sha(data: bytes) -> str:
    return f"sha256:{hashlib.sha256(data).hexdigest()}"


def _write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")


def _valid_inputs(tmp_path: Path) -> ReadinessInputs:
    root = tmp_path / "assets"
    knowledge = root / "story_cache" / "persona" / "mash"
    _write_json(
        knowledge / "profile.json",
        {
            "servant_id": 800100,
            "collection_no": 1,
            "name": "玛修",
            "summary": "玛修。认真温柔。",
            "facts": {
                "character": {
                    "value": "认真温柔。",
                    "source_region": "CN",
                    "source_path": "profile.comments",
                    "jp_fallback": False,
                }
            },
            "source_hashes": {"CN": "sha256:profile"},
        },
    )
    _write_json(
        knowledge / "knowledge-manifest.json",
        {"schema_version": 1, "story_index_schema_version": 1, "scene_count": 3},
    )
    (knowledge / "story.sqlite3").write_bytes(b"test-index")
    results = [
        {
            "id": f"case-{index}",
            "route": "story" if index == 0 else "profile",
            "window_count": 3 if index == 0 else 0,
            "estimated_tokens": 300 if index == 0 else 20,
            "coverage_gap": False,
        }
        for index in range(11)
    ]
    _write_json(
        knowledge / "scenario-report.json",
        {"schema_version": 1, "scenario_count": 11, "results": results},
    )
    (knowledge / "evidence.jsonl").write_text(
        json.dumps({"evidence_id": "e1", "review": {"status": "approved"}}) + "\n",
        encoding="utf-8",
    )

    art = root / "pet" / "mash" / "casual"
    source = root / "source.png"
    source.parent.mkdir(parents=True, exist_ok=True)
    source.write_bytes(b"source")
    assets = []
    ids = ["full_body"] + [
        f"r{row:02d}c{column:02d}"
        for row in range(1, 8)
        for column in range(1, 5)
    ]
    for stable_id in ids:
        raw = art / "raw" / f"{stable_id}.png"
        runtime = art / "runtime" / f"{stable_id}.png"
        raw.parent.mkdir(parents=True, exist_ok=True)
        runtime.parent.mkdir(parents=True, exist_ok=True)
        raw.write_bytes(f"raw-{stable_id}".encode())
        runtime.write_bytes(f"runtime-{stable_id}".encode())
        assets.append(
            {
                "stable_id": stable_id,
                "raw_path": f"raw/{stable_id}.png",
                "runtime_path": f"runtime/{stable_id}.png",
                "raw_sha256": _sha(raw.read_bytes()),
                "runtime_sha256": _sha(runtime.read_bytes()),
            }
        )
    _write_json(
        art / "manifest.json",
        {
            "schema_version": 1,
            "outfit_id": "mash_casual_98001000",
            "source": {"path": str(source), "sha256": _sha(source.read_bytes())},
            "assets": assets,
        },
    )
    _write_json(art / "qa-report.json", {"status": "PASS", "errors": []})
    return ReadinessInputs(data_root=root, visual_qa="approved")


def test_visual_qa_blocks_integration(tmp_path: Path) -> None:
    inputs = _valid_inputs(tmp_path).model_copy(update={"visual_qa": "pending"})

    result = evaluate_readiness(inputs)

    assert result.status == "BLOCKED"
    assert "art.visual_qa" in result.failed_checks


def test_all_checks_produce_pass(tmp_path: Path) -> None:
    result = evaluate_readiness(_valid_inputs(tmp_path))

    assert result.status == "PASS"
    assert result.failed_checks == []


def test_changed_artifact_hash_blocks_stale_review(tmp_path: Path) -> None:
    inputs = _valid_inputs(tmp_path)
    changed = inputs.data_root / "pet" / "mash" / "casual" / "runtime" / "r01c01.png"
    changed.write_bytes(b"changed")

    result = evaluate_readiness(inputs)

    assert result.status == "BLOCKED"
    assert "art.hashes" in result.failed_checks


def test_art_manifest_paths_cannot_escape_bundle(tmp_path: Path) -> None:
    inputs = _valid_inputs(tmp_path)
    art = inputs.data_root / "pet" / "mash" / "casual"
    manifest_path = art / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    escaped = inputs.data_root / "pet" / "outside.png"
    escaped.write_bytes(b"outside")
    manifest["assets"][1]["raw_path"] = "../../outside.png"
    manifest["assets"][1]["raw_sha256"] = _sha(escaped.read_bytes())
    _write_json(manifest_path, manifest)

    result = evaluate_readiness(inputs)

    assert result.status == "BLOCKED"
    assert "art.hashes" in result.failed_checks


def test_art_source_must_remain_inside_data_root(tmp_path: Path) -> None:
    inputs = _valid_inputs(tmp_path)
    manifest_path = inputs.data_root / "pet" / "mash" / "casual" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    outside = tmp_path / "outside-source.png"
    outside.write_bytes(b"outside-source")
    manifest["source"]["path"] = str(outside)
    manifest["source"]["sha256"] = _sha(outside.read_bytes())
    _write_json(manifest_path, manifest)

    result = evaluate_readiness(inputs)

    assert result.status == "BLOCKED"
    assert "art.hashes" in result.failed_checks
