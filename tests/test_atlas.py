import json
import os
from pathlib import Path

import httpx
import pytest
import respx

from fgo_pet_content.atlas import AtlasClient, ScriptUnavailable
from fgo_pet_content.config import ContentPaths
from fgo_pet_content.models.source import Region


@pytest.fixture
def paths(tmp_path: Path) -> ContentPaths:
    repo = tmp_path / "repo"
    repo.mkdir()
    return ContentPaths.from_root(tmp_path / "assets", repo)


@respx.mock
def test_search_scripts_parses_atlas_hits(paths: ContentPaths) -> None:
    fixture = Path("tests/fixtures/atlas_script_search_mash.json")
    payload = json.loads(fixture.read_text(encoding="utf-8"))
    respx.get("https://api.atlasacademy.io/nice/CN/script/search").mock(
        return_value=httpx.Response(200, json=payload)
    )

    hits = AtlasClient(paths).search_scripts(Region.CN, "玛修", limit=3)

    assert hits[0].script_id == "0200040010"
    assert hits[0].score == 130.0


@respx.mock
def test_fetch_script_writes_content_addressed_cache(paths: ContentPaths) -> None:
    script_url = "https://static.atlasacademy.io/CN/Script/02/0200040010.txt"
    respx.get("https://api.atlasacademy.io/nice/CN/script/0200040010").mock(
        return_value=httpx.Response(200, json={"scriptId": "0200040010", "script": script_url})
    )
    respx.get(script_url).mock(
        return_value=httpx.Response(200, text="＄02-00\n＠玛修\n早上好。\n[k]\n")
    )

    cached = AtlasClient(paths).fetch_script(Region.CN, "0200040010")

    assert cached.sha256.startswith("sha256:")
    assert cached.raw_path.read_text(encoding="utf-8").startswith("＄02-00")
    assert cached.metadata_path.exists()
    assert cached.raw_path.is_relative_to(paths.raw_scripts)


@respx.mock
def test_repeated_matching_hash_does_not_replace_raw_file(paths: ContentPaths) -> None:
    script_url = "https://static.atlasacademy.io/CN/Script/02/0200040010.txt"
    respx.get("https://api.atlasacademy.io/nice/CN/script/0200040010").mock(
        return_value=httpx.Response(200, json={"scriptId": "0200040010", "script": script_url})
    )
    respx.get(script_url).mock(return_value=httpx.Response(200, text="same text"))
    client = AtlasClient(paths)
    first = client.fetch_script(Region.CN, "0200040010")
    fixed_time = 1_700_000_000
    os.utime(first.raw_path, (fixed_time, fixed_time))

    second = client.fetch_script(Region.CN, "0200040010")

    assert second.raw_path == first.raw_path
    assert second.raw_path.stat().st_mtime == fixed_time


@respx.mock
def test_missing_script_raises_typed_error(paths: ContentPaths) -> None:
    respx.get("https://api.atlasacademy.io/nice/CN/script/missing").mock(
        return_value=httpx.Response(404, json={"detail": "Not Found"})
    )

    with pytest.raises(ScriptUnavailable) as error:
        AtlasClient(paths).fetch_script(Region.CN, "missing")

    assert error.value.region is Region.CN
    assert error.value.script_id == "missing"
