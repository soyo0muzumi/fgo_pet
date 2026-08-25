from fgo_pet_content.evidence.context import build_evidence_windows
from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance


def make_document() -> StoryDocument:
    return StoryDocument(
        source=SourceRef(
            region=Region.CN,
            script_id="script-1",
            container_type="quest",
            content_hash="sha256:x",
        ),
        scenes=[
            StoryScene(
                scene_index=1,
                utterances=[
                    Utterance(
                        order=index,
                        speaker="玛修" if index == 4 else "其他人",
                        servant_id=800100 if index == 4 else None,
                        text=f"台词 {index}",
                        raw_start_line=index,
                        raw_end_line=index,
                    )
                    for index in range(1, 9)
                ],
            )
        ],
    )


def test_window_includes_neighbor_context_around_mash_line() -> None:
    windows = build_evidence_windows(make_document(), servant_id=800100, neighbor_lines=3)

    assert windows[0].target_orders == (4,)
    assert [item.order for item in windows[0].utterances] == [1, 2, 3, 4, 5, 6, 7]
