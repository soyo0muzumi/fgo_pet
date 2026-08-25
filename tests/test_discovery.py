from fgo_pet_content.atlas import ScriptSearchHit
from fgo_pet_content.discovery import MashIdentity, discover_candidates, is_mash_related
from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance


class FakeSearchAtlas:
    def search_scripts(self, region: Region, query: str, limit: int = 100):
        return [
            ScriptSearchHit(
                script_id="0200040010",
                script_url=f"https://example.test/{region.value}/0200040010.txt",
                score=100.0,
                snippets=(query,),
            )
        ]


def test_candidate_discovery_deduplicates_name_hits() -> None:
    identity = MashIdentity.default()

    hits = discover_candidates(identity, FakeSearchAtlas())

    assert [hit.script_id for hit in hits] == ["0200040010"]
    assert hits[0].matched_regions == {Region.CN, Region.JP}
    assert hits[0].match_reasons == {"name:玛修", "name:マシュ"}


def test_figure_mapping_confirms_mash_related_document() -> None:
    identity = MashIdentity.default()
    source = SourceRef(
        region=Region.CN,
        script_id="x",
        container_type="quest",
        content_hash="sha256:x",
    )
    document = StoryDocument(
        source=source,
        scenes=[
            StoryScene(
                scene_index=1,
                utterances=[
                    Utterance(
                        order=1,
                        speaker="？？？",
                        servant_id=800100,
                        figure_id="98001000",
                        text="……",
                        raw_start_line=1,
                        raw_end_line=3,
                    )
                ],
            )
        ],
    )

    assert is_mash_related(document, identity)
