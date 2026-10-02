from __future__ import annotations

import hashlib
import json
from pathlib import Path

from PIL import Image


ROOT = Path("content/packs/official.mash")
APPEARANCE = ROOT / "appearances/casual"


def normalize(path: Path) -> None:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
    canvas = Image.new("RGBA", image.size, (0, 0, 0, 0))
    inner = image.resize((image.width - 2, image.height - 2), Image.Resampling.LANCZOS)
    canvas.paste(inner, (1, 1), inner)
    canvas.save(path, format="PNG", optimize=False, compress_level=9)


def main() -> None:
    paths = [
        APPEARANCE / "runtime/full_body.png",
        *sorted((APPEARANCE / "runtime/expressions").glob("*.png")),
    ]
    for path in paths:
        normalize(path)

    manifest_path = APPEARANCE / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for asset in manifest["assets"]:
        path = APPEARANCE / asset["path"]
        asset["sha256"] = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
