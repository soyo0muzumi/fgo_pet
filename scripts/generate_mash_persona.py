"""Generate the external, local-only Mash persona review artifacts."""

from __future__ import annotations

import argparse
from pathlib import Path

from fgo_pet_content.mash_persona import (
    DEFAULT_CHAPTERS,
    build_coverage,
    collect_hits,
    generate_persona_outputs,
)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--formatted", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    coverage = build_coverage(args.formatted, DEFAULT_CHAPTERS)
    hits = collect_hits(args.formatted, DEFAULT_CHAPTERS)
    outputs = generate_persona_outputs(args.output, hits, coverage)
    for name, path in outputs.items():
        print(f"{name}: {path}")


if __name__ == "__main__":
    main()

