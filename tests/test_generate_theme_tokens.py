import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tools.scripts import generate_theme_tokens as tokens


class GenerateThemeTokensTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.css = Path(self.temp.name) / "root.css"
        self.source = json.loads(tokens.read(tokens.SOURCE))

    def generate(self, current):
        self.css.write_text(current, encoding="utf-8")
        with patch.object(tokens, "THEMES", {}), patch.object(
            tokens, "CSS", {self.css: {"font": None}}
        ):
            return tokens.outputs(self.source)[self.css]

    def test_preserves_leading_comments_whitespace_and_following_rules(self):
        prefix = " \n/* :root { fake: value; } */\n/* second\ncomment */\n\t"
        suffix = "\nbody { color: var(--text); }\n"
        generated = self.generate(prefix + ":root { --sidebar-width:212px; }" + suffix)
        self.assertTrue(generated.startswith(prefix + ":root {\n"))
        self.assertTrue(generated.endswith(suffix))
        self.assertIn("  --sidebar-width:212px;\n", generated)

    def test_preserves_inline_unmanaged_declarations_and_replaces_tokens(self):
        generated = self.generate(
            ":root { --sidebar-width:212px; font:14px var(--ui); --accent: red; }\n"
        )
        self.assertIn("  --sidebar-width:212px;\n", generated)
        self.assertIn("  font:14px var(--ui);\n", generated)
        self.assertNotIn("--accent: red", generated)
        self.assertEqual(generated.count("--accent:"), 1)
        self.assertIn("  color-scheme: light dark;\n", generated)
        self.assertEqual(self.generate(generated), generated)

    def test_preserves_multiline_unmanaged_declarations(self):
        generated = self.generate(":root {\n  --sidebar-width: 212px;\n  font: 14px var(--ui);\n}\n")
        self.assertIn("  --sidebar-width: 212px;\n  font: 14px var(--ui);\n", generated)
        self.assertEqual(self.generate(generated), generated)

    def test_preserves_semicolons_inside_custom_values(self):
        declarations = (
            '--label: "left;right";',
            "--image: url(data:image/png;base64,AAAA);",
            "--custom: value /* keep; comment */;",
        )
        generated = self.generate(":root { " + " ".join(declarations) + " }\n")
        for declaration in declarations:
            self.assertIn("  " + declaration + "\n", generated)
        self.assertEqual(self.generate(generated), generated)

    def test_rejects_missing_root_or_root_after_other_rules(self):
        for current in (
            "/* :root { fake: value; } */\nbody {}\n",
            "body {}\n:root {}\n",
            "/* unfinished comment\n:root {}\n",
            ":root {\n",
        ):
            with self.subTest(current=current):
                with self.assertRaisesRegex(ValueError, "Missing CSS root"):
                    self.generate(current)

    def test_repository_outputs_are_current_and_idempotent(self):
        for path, generated in tokens.outputs(self.source).items():
            with self.subTest(path=path):
                self.assertEqual(tokens.read(path), generated)


if __name__ == "__main__":
    unittest.main()
