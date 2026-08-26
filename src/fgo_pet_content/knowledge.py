from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

from .atlas import AtlasClient
from .cache import atomic_write
from .config import ContentPaths
from .models.source import Region
from .models.story import StoryDocument
from .profile import MashProfile, build_profile
from .retrieval import StoryIndexManifest, build_story_index


def knowledge_dir(paths: ContentPaths) -> Path:
    return paths.story_cache / "persona" / "mash"


def _read_manifest(paths: ContentPaths) -> dict:
    path = knowledge_dir(paths) / "knowledge-manifest.json"
    if not path.exists():
        return {"schema_version": 1}
    return json.loads(path.read_text(encoding="utf-8"))


def _write_manifest(paths: ContentPaths, updates: dict) -> Path:
    payload = {
        **_read_manifest(paths),
        **updates,
        "built_at": datetime.now(timezone.utc).isoformat(),
    }
    destination = knowledge_dir(paths) / "knowledge-manifest.json"
    atomic_write(
        destination,
        json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8"),
    )
    return destination


def build_profile_artifact(
    paths: ContentPaths,
    atlas: AtlasClient,
    *,
    servant_id: int = 800100,
    collection_no: int = 1,
) -> tuple[MashProfile, Path]:
    cn = atlas.fetch_servant(Region.CN, collection_no, lore=True)
    jp = atlas.fetch_servant(Region.JP, collection_no, lore=True)
    profile = build_profile(cn, jp, servant_id=servant_id)
    destination = knowledge_dir(paths) / "profile.json"
    atomic_write(destination, profile.model_dump_json(indent=2).encode("utf-8"))
    _write_manifest(
        paths,
        {
            "profile_schema_version": 1,
            "profile_source_hashes": profile.source_hashes,
            "jp_fallback_count": sum(
                fact.jp_fallback for fact in profile.facts.values()
            ),
        },
    )
    return profile, destination


def load_formatted_documents(paths: ContentPaths) -> list[StoryDocument]:
    documents: list[StoryDocument] = []
    for path in sorted(paths.formatted_scripts.rglob("*.json")):
        if path.name == "index.json":
            continue
        payload = json.loads(path.read_text(encoding="utf-8"))
        documents.append(StoryDocument.model_validate(payload))
    return documents


def build_index_artifact(
    paths: ContentPaths,
) -> tuple[StoryIndexManifest, Path]:
    destination = knowledge_dir(paths) / "story.sqlite3"
    result = build_story_index(destination, load_formatted_documents(paths))
    _write_manifest(
        paths,
        {
            "story_index_schema_version": result.schema_version,
            "scene_count": result.scene_count,
        },
    )
    return result, destination
