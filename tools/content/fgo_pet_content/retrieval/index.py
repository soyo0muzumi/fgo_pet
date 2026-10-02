from __future__ import annotations

import json
import re
import sqlite3
from pathlib import Path

from ..models.source import SourceRef
from ..models.story import StoryDocument
from .models import StoryHit, StoryIndexManifest


_MASH_ALIASES = "玛修 马修 マシュ Mash"
_QUESTION_WORDS = ("为什么", "发生", "经历", "什么", "剧情", "详细", "告诉", "请问", "请", "在", "是", "了", "我")
_QUERY_ALIASES = {
    "黑色枪管": (
        "黑色枪管",
        "黑色铁炮管",
        "黑色炮身",
        "黑枪管",
        "人理测定炮",
        "测定炮",
        "Black Barrel",
    ),
}


def _search_tokens(text: str) -> list[str]:
    return re.findall(r"[\u3400-\u9fff]|[A-Za-z0-9]+", text.casefold())


def _search_text(text: str) -> str:
    return " ".join(_search_tokens(text))


def _topic_phrases(query: str) -> list[str]:
    cleaned = query
    for word in _QUESTION_WORDS:
        cleaned = cleaned.replace(word, " ")
    phrases = [
        item for item in re.findall(r"[\u3400-\u9fff]+", cleaned) if len(item) >= 3
    ]
    expanded: list[str] = []
    for phrase in phrases:
        expanded.extend(_QUERY_ALIASES.get(phrase, (phrase,)))
    return list(dict.fromkeys(expanded))


def build_story_index(
    database: Path, documents: list[StoryDocument]
) -> StoryIndexManifest:
    database.parent.mkdir(parents=True, exist_ok=True)
    temporary = database.with_suffix(database.suffix + ".tmp")
    if temporary.exists():
        temporary.unlink()

    connection = sqlite3.connect(temporary)
    try:
        connection.executescript(
            """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE scenes (
                scene_id TEXT PRIMARY KEY,
                scene_index INTEGER NOT NULL,
                source_json TEXT NOT NULL,
                speakers_json TEXT NOT NULL,
                text TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE scene_fts USING fts5(
                scene_id UNINDEXED,
                text,
                speakers,
                aliases,
                tokenize='unicode61'
            );
            CREATE VIRTUAL TABLE scene_trigram USING fts5(
                scene_id UNINDEXED,
                text,
                speakers,
                aliases,
                tokenize='trigram'
            );
            """
        )
        scene_count = 0
        for document in documents:
            for scene in document.scenes:
                scene_id = f"{document.source.script_id}:{scene.scene_index}"
                speakers = tuple(dict.fromkeys(item.speaker for item in scene.utterances))
                text = "\n".join(
                    f"{item.speaker}：{item.text}" for item in scene.utterances
                )
                connection.execute(
                    "INSERT INTO scenes VALUES (?, ?, ?, ?, ?)",
                    (
                        scene_id,
                        scene.scene_index,
                        document.source.model_dump_json(),
                        json.dumps(speakers, ensure_ascii=False),
                        text,
                    ),
                )
                connection.execute(
                    "INSERT INTO scene_fts VALUES (?, ?, ?, ?)",
                    (
                        scene_id,
                        _search_text(text),
                        _search_text(" ".join(speakers)),
                        _search_text(_MASH_ALIASES)
                        if any(name in {"玛修", "マシュ", "Mash"} for name in speakers)
                        else "",
                    ),
                )
                connection.execute(
                    "INSERT INTO scene_trigram VALUES (?, ?, ?, ?)",
                    (
                        scene_id,
                        text,
                        " ".join(speakers),
                        _MASH_ALIASES
                        if any(name in {"玛修", "マシュ", "Mash"} for name in speakers)
                        else "",
                    ),
                )
                scene_count += 1
        connection.executemany(
            "INSERT INTO metadata VALUES (?, ?)",
            (("schema_version", "1"), ("scene_count", str(scene_count))),
        )
        connection.commit()
    finally:
        connection.close()
    temporary.replace(database)
    return StoryIndexManifest(scene_count=scene_count)


def search_story_index(database: Path, query: str, *, limit: int = 8) -> list[StoryHit]:
    tokens = _search_tokens(query)
    if not tokens:
        return []
    connection = sqlite3.connect(database)
    try:
        topics = _topic_phrases(query)
        rows = []
        if topics:
            topic_expression = " OR ".join(f'"{topic}"' for topic in topics)
            rows = connection.execute(
                """
                SELECT s.scene_id, s.scene_index, s.source_json, s.speakers_json,
                       s.text, bm25(scene_trigram) AS score
                FROM scene_trigram
                JOIN scenes AS s ON s.scene_id = scene_trigram.scene_id
                WHERE scene_trigram MATCH ?
                ORDER BY score, s.scene_id
                LIMIT ?
                """,
                (topic_expression, limit),
            ).fetchall()
        if not rows:
            expression = " OR ".join(f'"{token}"' for token in tokens)
            rows = connection.execute(
                """
                SELECT s.scene_id, s.scene_index, s.source_json, s.speakers_json,
                       s.text, bm25(scene_fts) AS score
                FROM scene_fts
                JOIN scenes AS s ON s.scene_id = scene_fts.scene_id
                WHERE scene_fts MATCH ?
                ORDER BY score, s.scene_id
                LIMIT ?
                """,
                (expression, limit),
            ).fetchall()
    finally:
        connection.close()
    return [_row_to_hit(row) for row in rows]


def _row_to_hit(row: tuple) -> StoryHit:
    return StoryHit(
        scene_id=row[0],
        scene_index=row[1],
        source=SourceRef.model_validate_json(row[2]),
        speakers=tuple(json.loads(row[3])),
        text=row[4],
        score=row[5],
    )


def load_adjacent_story_hits(
    database: Path, hit: StoryHit
) -> list[StoryHit]:
    connection = sqlite3.connect(database)
    try:
        rows = connection.execute(
            """
            SELECT scene_id, scene_index, source_json, speakers_json, text, ?
            FROM scenes
            WHERE scene_id IN (?, ?)
            ORDER BY ABS(scene_index - ?), scene_index
            """,
            (
                hit.score + 1.0,
                f"{hit.source.script_id}:{hit.scene_index - 1}",
                f"{hit.source.script_id}:{hit.scene_index + 1}",
                hit.scene_index,
            ),
        ).fetchall()
    finally:
        connection.close()
    return [_row_to_hit(row) for row in rows]
