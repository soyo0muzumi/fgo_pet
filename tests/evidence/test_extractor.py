from fgo_pet_content.evidence.context import build_evidence_windows
from fgo_pet_content.evidence.extractor import EvidenceExtractor

from .test_context import make_document
from .test_validator import make_card


class FakeStructuredClient:
    def __init__(self) -> None:
        self.schema = None

    def generate(self, *, system: str, user: str, schema: dict) -> dict:
        self.schema = schema
        assert "只能依据" in system
        assert "utterance_order" in user
        return {"cards": [make_card().model_dump(mode="json")]}


def test_extractor_uses_schema_and_returns_valid_cards() -> None:
    client = FakeStructuredClient()
    window = build_evidence_windows(make_document(), 800100, 3)[0]

    cards = EvidenceExtractor(client).extract(window)

    assert cards[0].evidence_id == "ev-1"
    assert client.schema["title"] == "EvidenceBatch"
