from __future__ import annotations

import json
import os
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from tempfile import NamedTemporaryFile

from .config import ContentPaths
from .models.source import Region


@dataclass(frozen=True, slots=True)
class CachedScript:
    region: Region
    script_id: str
    sha256: str
    raw_path: Path
    metadata_path: Path
    source_url: str


def cache_script(
    paths: ContentPaths,
    region: Region,
    script_id: str,
    source_url: str,
    content: bytes,
    digest: str,
) -> CachedScript:
    digest_value = digest.removeprefix("sha256:")
    script_dir = paths.raw_scripts / region.value / script_id
    raw_path = script_dir / f"{digest_value}.txt"
    metadata_path = script_dir / f"{digest_value}.json"
    script_dir.mkdir(parents=True, exist_ok=True)

    if not raw_path.exists():
        atomic_write(raw_path, content)
    if not metadata_path.exists():
        metadata = {
            "region": region.value,
            "script_id": script_id,
            "source_url": source_url,
            "sha256": digest,
            "size_bytes": len(content),
            "fetched_at": datetime.now(UTC).isoformat(),
        }
        atomic_write(
            metadata_path,
            json.dumps(metadata, ensure_ascii=False, indent=2).encode("utf-8"),
        )

    return CachedScript(
        region=region,
        script_id=script_id,
        sha256=digest,
        raw_path=raw_path,
        metadata_path=metadata_path,
        source_url=source_url,
    )


def atomic_write(destination: Path, content: bytes) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=destination.parent, delete=False) as temporary:
        temporary.write(content)
        temporary.flush()
        os.fsync(temporary.fileno())
        temporary_path = Path(temporary.name)
    temporary_path.replace(destination)
