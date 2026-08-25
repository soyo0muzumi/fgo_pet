from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from .atlas import ScriptUnavailable
from .cache import atomic_write
from .config import ContentPaths
from .discovery import MashIdentity, ScriptCandidate, is_mash_related
from .models.source import Region, SourceRef, TranslationStatus
from .models.story import StoryDocument
from .parser import parse_story


@dataclass(frozen=True, slots=True)
class ParsedArtifact:
    document: StoryDocument
    translation_status: TranslationStatus
    is_related: bool


class StoryPipeline:
    def __init__(self, atlas, catalog, identity: MashIdentity) -> None:
        self._atlas = atlas
        self._catalog = catalog
        self._identity = identity

    def fetch_and_parse(self, candidate: ScriptCandidate) -> ParsedArtifact:
        cached, translation_status = self._fetch_with_fallback(candidate.script_id)
        source = self._resolve_source(candidate.script_id, cached.region).model_copy(
            update={
                "region": cached.region,
                "content_hash": cached.sha256,
                "source_url": cached.source_url,
            }
        )
        text = cached.raw_path.read_text(encoding="utf-8-sig")
        figure_map = {
            figure_id: self._identity.servant_id
            for figure_id in self._identity.figure_ids
        }
        document = parse_story(text, source, figure_map)
        return ParsedArtifact(
            document=document,
            translation_status=translation_status,
            is_related=is_mash_related(document, self._identity),
        )

    def _fetch_with_fallback(self, script_id: str):
        try:
            return (
                self._atlas.fetch_script(Region.CN, script_id),
                TranslationStatus.OFFICIAL_CN,
            )
        except ScriptUnavailable:
            return (
                self._atlas.fetch_script(Region.JP, script_id),
                TranslationStatus.JP_FALLBACK,
            )

    def _resolve_source(self, script_id: str, region: Region) -> SourceRef:
        refs = self._catalog.resolve(script_id)
        if refs:
            return refs[0]
        return SourceRef(
            region=region,
            script_id=script_id,
            container_type="unresolved",
        )


def write_parsed_artifact(
    artifact: ParsedArtifact, paths: ContentPaths
) -> Path:
    source = artifact.document.source
    digest = (source.content_hash or "sha256:unknown").removeprefix("sha256:")
    output = (
        paths.parsed_scripts
        / source.region.value
        / source.script_id
        / f"{digest}.json"
    )
    payload = {
        "translation_status": artifact.translation_status.value,
        "is_related": artifact.is_related,
        "document": artifact.document.model_dump(mode="json"),
    }
    atomic_write(
        output,
        json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8"),
    )
    return output
