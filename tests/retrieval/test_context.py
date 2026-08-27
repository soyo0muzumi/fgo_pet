from pathlib import Path

from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance
from fgo_pet_content.profile import build_profile
from fgo_pet_content.retrieval import build_story_index
from fgo_pet_content.retrieval.context import compose_context
from fgo_pet_content.retrieval.query import route_query


def _profile():
    return build_profile(
        {
            "id": 800100,
            "collectionNo": 1,
            "name": "玛修",
            "profile": {"character": "认真温柔。", "likes": "读书"},
        },
        None,
        servant_id=800100,
    )


def _index(tmp_path: Path) -> Path:
    database = tmp_path / "story.sqlite3"
    source = SourceRef(
        region=Region.CN,
        script_id="lb-1",
        container_type="war",
        content_hash="sha256:lb",
    )
    scenes = []
    for index, text in enumerate(
        (
            "黑色枪管的准备已经完成。",
            "黑色枪管需要玛修承担力量。",
            "黑色枪管用于对抗特殊威胁。",
            "黑色枪管启动之后战斗结束。",
        )
    ):
        scenes.append(
            StoryScene(
                scene_index=index,
                utterances=[
                    Utterance(
                        order=0,
                        speaker="玛修",
                        servant_id=800100,
                        text=text,
                        raw_start_line=index * 2 + 1,
                        raw_end_line=index * 2 + 2,
                    )
                ],
            )
        )
    build_story_index(database, [StoryDocument(source=source, scenes=scenes)])
    return database


def test_query_router_keeps_daily_and_profile_questions_out_of_story() -> None:
    profile = _profile()

    assert route_query("早上好", profile).route == "profile"
    assert route_query("你喜欢什么？", profile).route == "profile"
    assert route_query("黑色枪管是什么？", profile).route == "story"
    assert route_query("你在冬木说过这句话吗？", profile).route == "story"


def test_plot_question_uses_two_to_four_bounded_windows(tmp_path: Path) -> None:
    context = compose_context("黑色枪管是什么？", _profile(), _index(tmp_path))

    assert context.route == "story"
    assert 2 <= len(context.story_windows) <= 4
    assert context.estimated_tokens <= 900
    assert context.answer_sentence_range == (2, 4)
    assert context.expand_on_request is True
    assert all(window.source.content_hash == "sha256:lb" for window in context.story_windows)


class BrokenReranker:
    def rerank(self, query, hits):
        raise RuntimeError("offline")


def test_reranker_failure_keeps_fts_results(tmp_path: Path) -> None:
    context = compose_context(
        "黑色枪管是什么？",
        _profile(),
        _index(tmp_path),
        reranker=BrokenReranker(),
    )

    assert context.story_windows
    assert context.reranker_status == "fallback"


def test_missing_evidence_is_a_coverage_gap_not_character_ignorance(
    tmp_path: Path,
) -> None:
    context = compose_context("奥尔良剧情发生了什么？", _profile(), _index(tmp_path))

    assert context.coverage_gap is True
    assert "资料覆盖不足" in context.unsupported_detail_policy
    assert "不清楚" not in context.unsupported_detail_policy


def test_single_match_expands_to_adjacent_scene_windows(tmp_path: Path) -> None:
    database = tmp_path / "neighbors.sqlite3"
    source = SourceRef(
        region=Region.CN,
        script_id="neighbors",
        container_type="war",
        content_hash="sha256:neighbors",
    )
    texts = ("准备进入下一段。", "黑色铁炮管出现在眼前。", "众人继续前进。")
    scenes = [
        StoryScene(
            scene_index=index,
            utterances=[
                Utterance(
                    order=0,
                    speaker="玛修",
                    servant_id=800100,
                    text=text,
                    raw_start_line=index + 1,
                    raw_end_line=index + 1,
                )
            ],
        )
        for index, text in enumerate(texts)
    ]
    build_story_index(database, [StoryDocument(source=source, scenes=scenes)])

    context = compose_context("黑色枪管是什么？", _profile(), database)

    assert [window.scene_id for window in context.story_windows] == [
        "neighbors:1",
        "neighbors:0",
        "neighbors:2",
    ]
    assert context.coverage_gap is False
