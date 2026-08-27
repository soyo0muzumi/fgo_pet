from pathlib import Path

from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance
from fgo_pet_content.retrieval import build_story_index, search_story_index


def _document() -> StoryDocument:
    return StoryDocument(
        source=SourceRef(
            region=Region.CN,
            script_id="chapter-1",
            container_type="war",
            container_id=100,
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
                        text="前辈，今天也请多关照。",
                        raw_start_line=1,
                        raw_end_line=2,
                    )
                ],
            ),
            StoryScene(
                scene_index=1,
                utterances=[
                    Utterance(
                        order=0,
                        speaker="达·芬奇",
                        text="黑色枪管是用于对抗特殊威胁的装备。",
                        raw_start_line=3,
                        raw_end_line=4,
                    ),
                    Utterance(
                        order=1,
                        speaker="玛修",
                        servant_id=800100,
                        text="我会承担这份力量。",
                        raw_start_line=5,
                        raw_end_line=6,
                    ),
                ],
            ),
        ],
    )


def test_fts_returns_traceable_matching_scene(tmp_path: Path) -> None:
    database = tmp_path / "story.sqlite3"
    build_story_index(database, [_document()])

    hits = search_story_index(database, "黑色枪管", limit=8)

    assert hits[0].scene_id == "chapter-1:1"
    assert hits[0].source.region is Region.CN
    assert hits[0].source.content_hash == "sha256:story"
    assert "达·芬奇" in hits[0].speakers


def test_index_rebuild_is_idempotent_and_supports_aliases(tmp_path: Path) -> None:
    database = tmp_path / "story.sqlite3"

    first = build_story_index(database, [_document()])
    second = build_story_index(database, [_document()])
    hits = search_story_index(database, "马修 前辈", limit=8)

    assert first.scene_count == second.scene_count == 2
    assert first.schema_version == 1
    assert hits[0].scene_id == "chapter-1:0"


def test_fts_query_syntax_is_treated_as_plain_text(tmp_path: Path) -> None:
    database = tmp_path / "story.sqlite3"
    build_story_index(database, [_document()])

    assert search_story_index(database, '" OR NOT (', limit=8) == []


def test_chinese_topic_phrase_outranks_scattered_character_matches(
    tmp_path: Path,
) -> None:
    document = _document()
    document.scenes.insert(
        0,
        StoryScene(
            scene_index=9,
            utterances=[
                Utterance(
                    order=0,
                    speaker="路人",
                    text="黑云下的颜色很深，长枪旁放着一根管子。",
                    raw_start_line=10,
                    raw_end_line=11,
                )
            ],
        ),
    )
    database = tmp_path / "story.sqlite3"
    build_story_index(database, [document])

    hits = search_story_index(database, "黑色枪管是什么？", limit=8)

    assert hits[0].scene_id == "chapter-1:1"
    assert all(hit.scene_id != "chapter-1:9" for hit in hits)


def test_story_term_alias_matches_official_wording(tmp_path: Path) -> None:
    document = _document()
    document.scenes[1].utterances[0].text = "黑色铁炮管用于对抗特殊威胁。"
    document.scenes.insert(
        0,
        StoryScene(
            scene_index=9,
            utterances=[
                Utterance(
                    order=0,
                    speaker="路人",
                    text="黑云、颜色、长枪和管子都在仓库里。",
                    raw_start_line=10,
                    raw_end_line=11,
                )
            ],
        ),
    )
    database = tmp_path / "story.sqlite3"
    build_story_index(database, [document])

    hits = search_story_index(database, "黑色枪管是什么？", limit=8)

    assert hits[0].scene_id == "chapter-1:1"
    assert all(hit.scene_id != "chapter-1:9" for hit in hits)
