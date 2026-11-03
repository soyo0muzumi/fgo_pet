"""Record licenses and an exact file manifest for one newly built runtime directory."""
from __future__ import annotations

import hashlib
import importlib.metadata
import json
import pathlib
import re
import sys


def main() -> None:
    root = pathlib.Path(sys.argv[1]).resolve(strict=True)
    if not (root / "FgoPet.WebSearch.exe").is_file() or not (root / "_internal").is_dir():
        raise SystemExit("Expected a Windows onedir search helper.")
    licenses = root / "licenses"
    licenses.mkdir()
    for distribution in importlib.metadata.distributions():
        name = re.sub(r"[^A-Za-z0-9_.-]", "_", distribution.metadata["Name"])
        for item in distribution.files or []:
            if any(part.lower().startswith(("license", "copying", "notice")) for part in item.parts):
                source = pathlib.Path(distribution.locate_file(item))
                if source.is_file() and source.suffix.lower() not in (".py", ".pyc"):
                    text = source.read_text(encoding="utf-8", errors="replace")
                    target = licenses / (name + "-" + re.sub(r"[^A-Za-z0-9_.-]", "_", str(item)) + ".txt")
                    target.write_text(text, encoding="utf-8")
    # CPython itself is part of the bundled executable runtime.
    python_license = pathlib.Path(sys.base_prefix) / "LICENSE.txt"
    if not python_license.is_file():
        raise SystemExit("Cannot locate the CPython distribution LICENSE.txt.")
    (licenses / "Python-LICENSE.txt").write_bytes(python_license.read_bytes())
    dependencies = {dist.metadata["Name"]: dist.version for dist in importlib.metadata.distributions()}
    (licenses / "runtime-dependencies.txt").write_text(
        "\n".join(f"{name}=={value}" for name, value in sorted(dependencies.items())) + "\n", encoding="utf-8")
    files = []
    for path in sorted(root.rglob("*")):
        if path.is_file():
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest().upper()
            files.append({"path": path.relative_to(root).as_posix(),
                "sha256": digest, "size": path.stat().st_size})
    (root / "runtime-manifest.json").write_text(json.dumps({"schemaVersion": 1, "protocolVersion": 1,
        "provider": "duckduckgo", "dependencyVersion": "9.16.1", "files": files}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
