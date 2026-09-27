from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

from .cache import atomic_write
from .config import ContentPaths
from .corpus import RegionalWarScript, StoryArc
from .discovery import MashIdentity
from .models.source import SourceRef
from .parser import parse_story
from .story_markdown import render_story_markdown


@dataclass(frozen=True, slots=True)
class ArcExportResult:
    output_dir: Path
    index_path: Path
    completed: int
    failed: int


def export_arc(
    arc: StoryArc,
    scripts: list[RegionalWarScript],
    atlas,
    paths: ContentPaths,
) -> ArcExportResult:
    output_dir = paths.formatted_scripts / arc.slug
    index_path = output_dir / "index.json"
    records: list[dict] = []
    figure_map = {
        figure_id: MashIdentity.default().servant_id
        for figure_id in MashIdentity.default().figure_ids
    }
    for order, regional in enumerate(scripts, start=1):
        script = regional.script
        record = {
            **asdict(script),
            "region": regional.region.value,
            "status": "pending",
            "json_path": None,
            "markdown_path": None,
            "error": None,
        }
        records.append(record)
        try:
            cached = atlas.load_cached_script(regional.region, script.script_id)
            if cached is None:
                cached = atlas.fetch_script_url(
                    regional.region, script.script_id, script.script_url
                )
            source = SourceRef(
                region=regional.region,
                script_id=script.script_id,
                container_type="quest" if script.quest_id else "war_opening",
                container_id=script.quest_id or script.war_id,
                container_name=script.quest_name or script.war_name,
                content_hash=cached.sha256,
                source_url=cached.source_url,
            )
            document = parse_story(
                cached.raw_path.read_text(encoding="utf-8-sig"),
                source,
                figure_map,
            )
            stem = f"{order:04d}-{script.script_id}"
            json_path = output_dir / f"{stem}.json"
            markdown_path = output_dir / f"{stem}.md"
            atomic_write(
                json_path,
                document.model_dump_json(indent=2).encode("utf-8"),
            )
            atomic_write(
                markdown_path,
                render_story_markdown(
                    document, chapter_title=arc.display_name
                ).encode("utf-8"),
            )
            record.update(
                status="completed",
                json_path=str(json_path),
                markdown_path=str(markdown_path),
            )
        except Exception as error:
            record.update(status="failed", error=str(error))
        _write_index(index_path, arc, records)
    completed = sum(item["status"] == "completed" for item in records)
    return ArcExportResult(
        output_dir=output_dir,
        index_path=index_path,
        completed=completed,
        failed=len(records) - completed,
    )


def _write_index(
    destination: Path, arc: StoryArc, records: list[dict]
) -> None:
    payload = {
        "arc": asdict(arc),
        "script_count": len(records),
        "completed": sum(item["status"] == "completed" for item in records),
        "failed": sum(item["status"] == "failed" for item in records),
        "scripts": records,
    }
    atomic_write(
        destination,
        json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8"),
    )
