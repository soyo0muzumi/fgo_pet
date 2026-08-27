import json
from pathlib import Path

from typer.testing import CliRunner

from fgo_pet_content.cli import app
from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance


runner = CliRunner()


def _write_lore_cache(root: Path) -> None:
    for region, name, likes in (
        ("CN", "玛修", ""),
        ("JP", "マシュ", "読書"),
    ):
        path = root / "story_cache" / "raw" / "servants" / region / "1-lore.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps(
                {
                    "id": 800100,
                    "collectionNo": 1,
                    "name": name,
                    "profile": {"character": "认真温柔。", "likes": likes},
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )


def _write_story(root: Path) -> None:
    output = root / "story_cache" / "formatted" / "arc" / "0001-story.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    document = StoryDocument(
        source=SourceRef(
            region=Region.CN,
            script_id="story",
            container_type="war",
            content_hash="sha256:story",
        ),
        scenes=[
            StoryScene(
                scene_index=0,
                utterances=[
                    Utterance(
                        order=0,
                        speaker="玛修",
                        servant_id=800100,
                        text="黑色枪管准备完成。",
                        raw_start_line=1,
                        raw_end_line=2,
                    )
                ],
            )
        ],
    )
    output.write_text(document.model_dump_json(), encoding="utf-8")


def test_knowledge_commands_build_external_artifacts_and_search(tmp_path: Path) -> None:
    data_root = tmp_path / "assets"
    _write_lore_cache(data_root)
    _write_story(data_root)

    profile_result = runner.invoke(
        app,
        [
            "knowledge",
            "build-profile",
            "--data-root",
            str(data_root),
            "--servant",
            "800100",
            "--collection-no",
            "1",
        ],
    )
    index_result = runner.invoke(
        app,
        ["knowledge", "build-index", "--data-root", str(data_root)],
    )
    search_result = runner.invoke(
        app,
        [
            "knowledge",
            "search",
            "--data-root",
            str(data_root),
            "--query",
            "黑色枪管",
        ],
    )
    evaluation_result = runner.invoke(
        app,
        [
            "knowledge",
            "evaluate-scenarios",
            "--data-root",
            str(data_root),
            "--cases",
            "tests/fixtures/mash_prompt_cases.json",
        ],
    )

    assert profile_result.exit_code == 0, profile_result.output
    assert index_result.exit_code == 0, index_result.output
    assert search_result.exit_code == 0, search_result.output
    assert evaluation_result.exit_code == 0, evaluation_result.output
    output_dir = data_root / "story_cache" / "persona" / "mash"
    assert json.loads((output_dir / "profile.json").read_text(encoding="utf-8"))[
        "facts"
    ]["likes"]["jp_fallback"] is True
    assert (output_dir / "story.sqlite3").exists()
    manifest = json.loads(
        (output_dir / "knowledge-manifest.json").read_text(encoding="utf-8")
    )
    assert manifest["scene_count"] == 1
    search_payload = json.loads(search_result.output)
    assert search_payload["hits"][0]["scene_id"] == "story:0"
    assert "黑色枪管准备完成。" not in search_result.output
    scenario_report = json.loads(
        (output_dir / "scenario-report.json").read_text(encoding="utf-8")
    )
    assert scenario_report["scenario_count"] == 11
    assert all(item["estimated_tokens"] <= 900 for item in scenario_report["results"])
