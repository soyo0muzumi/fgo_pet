import json
from pathlib import Path

from fgo_pet_content.cache import cache_script
from fgo_pet_content.config import ContentPaths
from fgo_pet_content.corpus import RegionalWarScript, StoryArc, WarScript
from fgo_pet_content.corpus_export import export_arc
from fgo_pet_content.models.source import Region


class FakeAtlas:
    def __init__(self, paths: ContentPaths) -> None:
        self.paths = paths

    def load_cached_script(self, region: Region, script_id: str):
        return None

    def fetch_script_url(self, region: Region, script_id: str, script_url: str):
        return cache_script(
            self.paths,
            region,
            script_id,
            script_url,
            "＠玛修\n早上好。\n[k]\n".encode(),
            "sha256:test",
        )


def test_export_arc_writes_json_markdown_and_index(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    paths = ContentPaths.from_root(tmp_path / "assets", repo)
    arc = StoryArc(100, "singularity-f", "特异点F")
    scripts = [
        RegionalWarScript(
            Region.CN,
            WarScript(
                100,
                "特异点F",
                "0100000010",
                "https://example.test/story.txt",
                quest_id=1000001,
                quest_name="序节",
                phases=(1,),
            ),
        )
    ]

    result = export_arc(arc, scripts, FakeAtlas(paths), paths)

    assert result.completed == 1
    markdown = next(result.output_dir.glob("*.md")).read_text(encoding="utf-8")
    assert "# 特异点F — 序节" in markdown
    assert "**玛修**：早上好。" in markdown
    payload = json.loads(result.index_path.read_text(encoding="utf-8"))
    assert payload["scripts"][0]["status"] == "completed"
    assert Path(payload["scripts"][0]["json_path"]).exists()
