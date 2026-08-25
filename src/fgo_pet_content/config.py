from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class ContentPaths:
    """Resolved locations for generated content outside the repository."""

    data_root: Path
    story_cache: Path
    raw_scripts: Path
    parsed_scripts: Path
    catalog: Path
    reports: Path
    art_workspace: Path

    @classmethod
    def from_root(cls, root: Path, repo_root: Path) -> ContentPaths:
        data_root = root.resolve()
        resolved_repo = repo_root.resolve()
        if data_root == resolved_repo or data_root.is_relative_to(resolved_repo):
            raise ValueError("content data root must be outside the repository")

        story_cache = data_root / "story_cache"
        return cls(
            data_root=data_root,
            story_cache=story_cache,
            raw_scripts=story_cache / "raw",
            parsed_scripts=story_cache / "parsed",
            catalog=story_cache / "catalog",
            reports=story_cache / "reports",
            art_workspace=data_root / "art_workspace",
        )
