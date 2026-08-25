from __future__ import annotations

import hashlib
from dataclasses import dataclass

import httpx

from .cache import CachedScript, cache_script
from .config import ContentPaths
from .models.source import Region


SEARCH_URL = "https://api.atlasacademy.io/nice/{region}/script/search"
SCRIPT_INFO_URL = "https://api.atlasacademy.io/nice/{region}/script/{script_id}"


@dataclass(frozen=True, slots=True)
class ScriptSearchHit:
    script_id: str
    script_url: str
    score: float
    snippets: tuple[str, ...]


class ScriptUnavailable(RuntimeError):
    def __init__(self, region: Region, script_id: str, status_code: int) -> None:
        super().__init__(f"script {script_id} is unavailable in {region.value} ({status_code})")
        self.region = region
        self.script_id = script_id
        self.status_code = status_code


class AtlasClient:
    def __init__(self, paths: ContentPaths, timeout_seconds: float = 30.0) -> None:
        self._paths = paths
        self._timeout = timeout_seconds

    def search_scripts(
        self, region: Region, query: str, limit: int = 100
    ) -> list[ScriptSearchHit]:
        response = httpx.get(
            SEARCH_URL.format(region=region.value),
            params={"query": query, "limit": limit},
            timeout=self._timeout,
            follow_redirects=True,
        )
        response.raise_for_status()
        return [
            ScriptSearchHit(
                script_id=item["scriptId"],
                script_url=item["script"],
                score=float(item["score"]),
                snippets=tuple(item.get("snippets", [])),
            )
            for item in response.json()
        ]

    def fetch_script(self, region: Region, script_id: str) -> CachedScript:
        info_response = httpx.get(
            SCRIPT_INFO_URL.format(region=region.value, script_id=script_id),
            timeout=self._timeout,
            follow_redirects=True,
        )
        if info_response.status_code == 404:
            raise ScriptUnavailable(region, script_id, 404)
        info_response.raise_for_status()
        script_url = info_response.json()["script"]

        script_response = httpx.get(
            script_url,
            timeout=self._timeout,
            follow_redirects=True,
        )
        if script_response.status_code == 404:
            raise ScriptUnavailable(region, script_id, 404)
        script_response.raise_for_status()
        content = script_response.content
        content.decode("utf-8-sig")
        digest = f"sha256:{hashlib.sha256(content).hexdigest()}"
        return cache_script(
            self._paths,
            region,
            script_id,
            script_url,
            content,
            digest,
        )
