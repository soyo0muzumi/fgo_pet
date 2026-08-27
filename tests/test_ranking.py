from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance
from fgo_pet_content.ranking import (
    ChapterMetrics,
    measure_chapter,
    rank_chapters,
    select_balanced,
)


def metric(
    script_id: str,
    category: str,
    *,
    mash_lines: int,
    total_lines: int = 100,
    faces: int = 3,
    atlas_score: float = 50,
) -> ChapterMetrics:
    return ChapterMetrics(
        script_id=script_id,
        region="CN",
        container_type="quest",
        container_id=1,
        container_name=f"chapter-{script_id}",
        category=category,
        mash_utterance_count=mash_lines,
        total_utterance_count=total_lines,
        unique_mash_faces=faces,
        atlas_score=atlas_score,
    )


def test_ranking_rewards_mash_density_and_expression_variety() -> None:
    dense = metric("dense", "core_growth", mash_lines=60, faces=8)
    sparse = metric("sparse", "core_growth", mash_lines=5, faces=1)

    ranked = rank_chapters([sparse, dense])

    assert [item.script_id for item in ranked] == ["dense", "sparse"]
    assert ranked[0].selection_score > ranked[1].selection_score


def test_balanced_selection_respects_category_quotas() -> None:
    ranked = rank_chapters(
        [
            metric("core-1", "core_growth", mash_lines=60),
            metric("core-2", "core_growth", mash_lines=50),
            metric("daily-1", "daily", mash_lines=40),
            metric("special-1", "special", mash_lines=30),
        ]
    )

    selected = select_balanced(
        ranked,
        quotas={"core_growth": 1, "daily": 1, "special": 1},
    )

    assert {item.script_id for item in selected} == {
        "core-1",
        "daily-1",
        "special-1",
    }


def test_candidate_record_contains_no_dialogue_text() -> None:
    candidate = rank_chapters([metric("safe", "unclassified", mash_lines=10)])[0]

    assert not hasattr(candidate, "text")
    assert not hasattr(candidate, "utterances")


def test_measure_chapter_counts_only_mash_lines_and_faces() -> None:
    document = StoryDocument(
        source=SourceRef(
            region=Region.CN,
            script_id="measured",
            container_type="quest",
            container_id=2,
            container_name="章节",
            content_hash="sha256:x",
        ),
        scenes=[
            StoryScene(
                scene_index=1,
                utterances=[
                    Utterance(order=1, speaker="玛修", face_id=7, text="a", raw_start_line=1, raw_end_line=1),
                    Utterance(order=2, speaker="玛修", face_id=13, text="b", raw_start_line=2, raw_end_line=2),
                    Utterance(order=3, speaker="其他", face_id=1, text="c", raw_start_line=3, raw_end_line=3),
                ],
            )
        ],
    )

    result = measure_chapter(document, atlas_score=90)

    assert result.mash_utterance_count == 2
    assert result.total_utterance_count == 3
    assert result.unique_mash_faces == 2
