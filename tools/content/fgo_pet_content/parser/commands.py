from __future__ import annotations

import re
from dataclasses import dataclass


COMMAND_PATTERN = re.compile(r"^\[([^\s\]]+)(?:\s+(.*?))?\]$")
INLINE_COMMAND_PATTERN = re.compile(r"\[[^\]]+\]")


@dataclass(frozen=True, slots=True)
class ParsedCommand:
    name: str
    arguments: tuple[str, ...]
    raw_arguments: str


def parse_command(line: str) -> ParsedCommand | None:
    match = COMMAND_PATTERN.fullmatch(line.strip())
    if match is None:
        return None
    raw_arguments = match.group(2) or ""
    return ParsedCommand(
        name=match.group(1),
        arguments=tuple(raw_arguments.split()),
        raw_arguments=raw_arguments,
    )


def normalize_dialogue(parts: list[str]) -> str:
    text = "\n".join(parts).replace("[r]", "\n")
    text = INLINE_COMMAND_PATTERN.sub("", text)
    return "\n".join(line.strip() for line in text.splitlines()).strip()
