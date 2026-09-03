from pathlib import Path


SCRIPT = Path(__file__).parents[2] / "scripts" / "test-release-candidate.ps1"


def test_uninstall_sentinel_is_outside_installer_owned_state_directory():
    text = SCRIPT.read_text(encoding="utf-8")
    assert "$sentinel = Join-Path $stateRoot 'acceptance-preserve.txt'" in text
    assert "$sentinel = Join-Path (Join-Path $stateRoot 'CodexAdapter')" not in text
