from pathlib import Path

from fgo_pet_content.atlas import ScriptUnavailable
from fgo_pet_content.cache import CachedScript
from fgo_pet_content.config import ContentPaths
from fgo_pet_content.discovery import MashIdentity, ScriptCandidate
from fgo_pet_content.models.source import Region, SourceRef, TranslationStatus
from fgo_pet_content.pipeline import StoryPipeline, write_parsed_artifact


class FallbackAtlas:
    def __init__(self, raw_path: Path) -> None:
        self.raw_path = raw_path
        self.calls: list[Region] = []

    def load_cached_script(
        self, region: Region, script_id: str
    ) -> CachedScript | None:
        return None

    def fetch_script(self, region: Region, script_id: str) -> CachedScript:
        self.calls.append(region)
        if region is Region.CN:
            raise ScriptUnavailable(region, script_id, 404)
        return CachedScript(
            region=region,
            script_id=script_id,
            sha256="sha256:jp",
            raw_path=self.raw_path,
            metadata_path=self.raw_path.with_suffix(".json"),
            source_url="https://example.test/JP/x.txt",
        )


class FakeCatalog:
    def resolve(self, script_id: str) -> list[SourceRef]:
        return [
            SourceRef(
                region=Region.JP,
                script_id=script_id,
                container_type="quest",
                container_id=1,
            )
        ]


def test_cn_missing_uses_jp_and_marks_translation_status(tmp_path: Path) -> None:
    raw_path = tmp_path / "x.txt"
    raw_path.write_text(
        "[charaSet B 98001000 1 マシュ]\n[charaTalk B]\n＠マシュ\nはい。\n[k]\n",
        encoding="utf-8",
    )
    atlas = FallbackAtlas(raw_path)
    pipeline = StoryPipeline(atlas, FakeCatalog(), MashIdentity.default())

    artifact = pipeline.fetch_and_parse(ScriptCandidate(script_id="x"))

    assert atlas.calls == [Region.CN, Region.JP]
    assert artifact.document.source.region is Region.JP
    assert artifact.document.source.content_hash == "sha256:jp"
    assert artifact.translation_status is TranslationStatus.JP_FALLBACK
    assert artifact.is_related


def test_cached_cn_script_is_used_without_network(tmp_path: Path) -> None:
    raw_path = tmp_path / "x.txt"
    raw_path.write_text("＠玛修\n好的。\n[k]\n", encoding="utf-8")

    class CachedAtlas(FallbackAtlas):
        def load_cached_script(
            self, region: Region, script_id: str
        ) -> CachedScript | None:
            if region is not Region.CN:
                return None
            return CachedScript(
                region=region,
                script_id=script_id,
                sha256="sha256:cn",
                raw_path=raw_path,
                metadata_path=raw_path.with_suffix(".json"),
                source_url="https://example.test/CN/x.txt",
            )

    atlas = CachedAtlas(raw_path)
    artifact = StoryPipeline(
        atlas, FakeCatalog(), MashIdentity.default()
    ).fetch_and_parse(ScriptCandidate(script_id="x"))

    assert atlas.calls == []
    assert artifact.document.source.region is Region.CN
    assert artifact.translation_status is TranslationStatus.OFFICIAL_CN


def test_parsed_artifact_is_written_only_to_external_cache(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    paths = ContentPaths.from_root(tmp_path / "assets", repo)
    raw_path = tmp_path / "x.txt"
    raw_path.write_text("＠玛修\n好的。\n[k]\n", encoding="utf-8")
    pipeline = StoryPipeline(FallbackAtlas(raw_path), FakeCatalog(), MashIdentity.default())
    artifact = pipeline.fetch_and_parse(ScriptCandidate(script_id="x"))

    output = write_parsed_artifact(artifact, paths)

    assert output.is_relative_to(paths.parsed_scripts)
    assert output.read_text(encoding="utf-8").startswith("{")
