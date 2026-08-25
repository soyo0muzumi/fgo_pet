from pathlib import Path

import pytest

from fgo_pet_content.config import ContentPaths


def test_external_data_root_cannot_be_inside_repo(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()

    with pytest.raises(ValueError, match="outside the repository"):
        ContentPaths.from_root(repo / "story_cache", repo)


def test_content_paths_create_expected_external_layout(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    data = tmp_path / "fgo_assets"
    repo.mkdir()

    paths = ContentPaths.from_root(data, repo)

    assert paths.raw_scripts == data / "story_cache" / "raw"
    assert paths.parsed_scripts == data / "story_cache" / "parsed"
