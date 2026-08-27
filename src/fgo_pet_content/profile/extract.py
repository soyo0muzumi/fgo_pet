from __future__ import annotations

import hashlib
import html
import json
import re
from typing import Any

from ..models.source import Region
from .models import MashProfile, ProfileFact, ProfileUnavailable


_FACT_PATHS = (
    ("character", "profile.character"),
    ("likes", "profile.likes"),
    ("dislikes", "profile.dislikes"),
    ("stats", "profile.stats"),
    ("comments", "profile.comments"),
)

_STAT_LABELS = (
    ("strength", "筋力"),
    ("endurance", "耐久"),
    ("agility", "敏捷"),
    ("magic", "魔力"),
    ("luck", "幸运"),
    ("np", "宝具"),
    ("policy", "阵营"),
    ("personality", "性格"),
)
_STAT_VALUES = {"lawful": "秩序", "good": "善", "neutral": "中立", "evil": "恶"}


def _source_hash(payload: dict[str, Any]) -> str:
    encoded = json.dumps(
        payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def _read_path(payload: dict[str, Any], path: str) -> Any:
    value: Any = payload
    for part in path.split("."):
        if not isinstance(value, dict):
            return None
        value = value.get(part)
    return value


def _normalize(value: Any) -> str:
    if isinstance(value, list):
        parts = []
        for item in value:
            if isinstance(item, dict):
                item = item.get("comment") or item.get("value") or ""
            cleaned = _normalize(item)
            if cleaned:
                parts.append(cleaned)
        return " ".join(parts)
    if not isinstance(value, str):
        return ""
    without_tags = re.sub(r"<[^>]+>", "", html.unescape(value))
    return re.sub(r"\s+", " ", without_tags).strip()


def _normalize_fact(key: str, value: Any) -> str:
    if key == "comments" and isinstance(value, list):
        base = next(
            (
                item
                for item in value
                if isinstance(item, dict) and item.get("condType") == "none"
            ),
            value[0] if value else None,
        )
        return _normalize(base.get("comment") if isinstance(base, dict) else base)
    if key == "stats" and isinstance(value, dict):
        parts = []
        for field, label in _STAT_LABELS:
            raw = value.get(field)
            if raw not in (None, "", "None"):
                parts.append(f"{label}{_STAT_VALUES.get(str(raw), raw)}")
        return "；".join(parts)
    return _normalize(value)


def _bounded_summary(name: str, facts: dict[str, ProfileFact], limit: int = 400) -> str:
    sentences = [f"{name}。"]
    for key, _ in _FACT_PATHS:
        if key in facts:
            value = facts[key].value
            sentences.append(value if value.endswith(("。", "！", "？")) else f"{value}。")
    summary = "".join(sentences)
    if len(summary) <= limit:
        return summary
    boundary = max(summary.rfind(mark, 0, limit + 1) for mark in "。！？")
    return summary[: boundary + 1] if boundary >= 0 else summary[: limit - 1] + "。"


def build_profile(
    cn_payload: dict[str, Any] | None,
    jp_payload: dict[str, Any] | None,
    *,
    servant_id: int,
) -> MashProfile:
    cn = cn_payload or {}
    jp = jp_payload or {}
    if not isinstance(cn.get("profile"), dict) and not isinstance(jp.get("profile"), dict):
        raise ProfileUnavailable("lore-enabled profile data is unavailable")

    facts: dict[str, ProfileFact] = {}
    for key, path in _FACT_PATHS:
        cn_value = _normalize_fact(key, _read_path(cn, path))
        jp_value = _normalize_fact(key, _read_path(jp, path))
        if cn_value:
            facts[key] = ProfileFact(
                value=cn_value,
                source_region=Region.CN,
                source_path=path,
            )
        elif jp_value:
            facts[key] = ProfileFact(
                value=jp_value,
                source_region=Region.JP,
                source_path=path,
                jp_fallback=True,
            )
    if not facts:
        raise ProfileUnavailable("profile contains no supported facts")

    name = _normalize(cn.get("name")) or _normalize(jp.get("name"))
    source_hashes = {
        region.value: _source_hash(payload)
        for region, payload in ((Region.CN, cn), (Region.JP, jp))
        if payload
    }
    return MashProfile(
        servant_id=servant_id,
        collection_no=1,
        name=name,
        summary=_bounded_summary(name, facts),
        facts=facts,
        source_hashes=source_hashes,
    )
