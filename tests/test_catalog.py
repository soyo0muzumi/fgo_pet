from pathlib import Path

import pytest

from fgo_pet_content.catalog import SourceCatalog
from fgo_pet_content.master_tables import MasterTableError, MasterTableReader
from fgo_pet_content.models.source import Region


@pytest.fixture
def master_root() -> Path:
    return Path("tests/fixtures/master")


def test_extensionless_json_master_table_is_read(master_root: Path) -> None:
    rows = MasterTableReader(master_root).read("mstWar")

    assert rows[0]["scriptId"] == "0200040010"


def test_war_opening_script_resolves_without_quest_link(master_root: Path) -> None:
    refs = SourceCatalog(MasterTableReader(master_root), Region.JP).resolve(
        "0200040010"
    )

    assert [(ref.container_type, ref.container_id) for ref in refs] == [
        ("war_opening", 204)
    ]
    assert refs[0].container_name == "亜種特異点Ⅳ"
    assert refs[0].content_hash is None


def test_reader_rejects_non_json_content(tmp_path: Path) -> None:
    (tmp_path / "mstWar").write_text("not-json", encoding="utf-8")

    with pytest.raises(MasterTableError, match="does not contain JSON"):
        MasterTableReader(tmp_path).read("mstWar")
