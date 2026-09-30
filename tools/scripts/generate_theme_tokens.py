"""Generate the WPF color palettes and Web fallback colors from one source.

Run with --check in CI. The Web host still sends the current WPF palette at
runtime; CSS values are only the safe initial colors before its first message.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "design" / "tokens.json"
THEMES = {
    "light": ROOT / "src/FgoPet.UiSdk/Resources/Themes/FgoLight.xaml",
    "dark": ROOT / "src/FgoPet.UiSdk/Resources/Themes/ModernGray.xaml",
}
CSS = {
    ROOT / "plugins/FgoPet.Plugin.Todo/Desktop/ui/todo/shared/todo.css": {
        "font": '"Segoe UI Variable", "Segoe UI", "Microsoft YaHei UI", sans-serif',
    },
    ROOT / "src/FgoPet.Desktop/Shell/Desktop/ui/settings/user-profile/profile.css": {
        "font": None,
    },
}
CSS_KEYS = {
    "--surface": "Semantic.ContentColor",
    "--page": "Semantic.SubtleColor",
    "--text": "Semantic.PrimaryTextColor",
    "--muted": "Semantic.SecondaryTextColor",
    "--line": "Semantic.BorderDecorativeColor",
    "--accent": "Semantic.PrimaryActionColor",
    "--on-accent": "Semantic.OnPrimaryActionColor",
    "--danger": "DangerColor",
}
COLORS = re.compile(r'    <Color x:Key="([^"]+)">([^<]+)</Color>')
CSS_ROOT = re.compile(r"\A:root\s*\{[^}]*\}", re.S)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write_if_changed(path: Path, content: str) -> None:
    if read(path) != content:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8", newline="\n")


def bootstrap() -> None:
    palettes = {name: dict(COLORS.findall(read(path))) for name, path in THEMES.items()}
    if palettes["light"].keys() != palettes["dark"].keys():
        raise ValueError("Light and dark color keys differ")
    source = {
        "colorKeys": list(palettes["light"]),
        "light": palettes["light"],
        "dark": palettes["dark"],
    }
    SOURCE.parent.mkdir(parents=True, exist_ok=True)
    SOURCE.write_text(json.dumps(source, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def css_color(argb: str) -> str:
    if not re.fullmatch(r"#[0-9A-Fa-f]{8}", argb):
        raise ValueError(f"Invalid ARGB color: {argb}")
    alpha = int(argb[1:3], 16)
    rgb = argb[3:].lower()
    return f"#{rgb}" if alpha == 255 else f"rgb({int(rgb[0:2], 16)} {int(rgb[2:4], 16)} {int(rgb[4:6], 16)} / {alpha / 255:.3f})"


def outputs(source: dict) -> dict[Path, str]:
    keys = source["colorKeys"]
    if len(keys) != len(set(keys)) or not keys:
        raise ValueError("Color keys must be unique and nonempty")
    generated = {}
    for name, path in THEMES.items():
        palette = source[name]
        if set(palette) != set(keys):
            raise ValueError(f"{name} palette keys differ from colorKeys")
        current = read(path)
        matches = list(COLORS.finditer(current))
        if not matches or {match.group(1) for match in matches} != set(keys):
            raise ValueError(f"Unexpected color resources in {path}")
        lines = [f'    <Color x:Key="{key}">{palette[key]}</Color>' for key in keys]
        for value in palette.values():
            css_color(value)
        generated[path] = current[:matches[0].start()] + "\n".join(lines) + current[matches[-1].end():]

    palette = source["light"]
    for path, config in CSS.items():
        current = read(path)
        root = CSS_ROOT.match(current)
        if root is None:
            raise ValueError(f"Missing CSS root in {path}")
        lines = [":root {", "  color-scheme: light dark;"]
        lines += [f"  {variable}: {css_color(palette[key])};" for variable, key in CSS_KEYS.items()]
        if config["font"]:
            lines += [f'  font-family: {config["font"]};', "  font-size: 14px;"]
        lines.append("}")
        generated[path] = "\n".join(lines) + current[root.end():]
    return generated


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if generated files drift")
    parser.add_argument("--bootstrap", action="store_true", help="import the existing palettes once")
    args = parser.parse_args()
    if args.bootstrap:
        if SOURCE.exists():
            parser.error("tokens.json already exists")
        bootstrap()
    source = json.loads(read(SOURCE))
    generated = outputs(source)
    drift = [str(path.relative_to(ROOT)) for path, content in generated.items() if read(path) != content]
    if args.check:
        if drift:
            print("Theme token drift: " + ", ".join(drift))
            return 1
        print("Theme tokens match generated palettes and CSS")
    else:
        for path, content in generated.items():
            write_if_changed(path, content)
        print("Generated theme tokens: " + ", ".join(drift or ["already current"]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
