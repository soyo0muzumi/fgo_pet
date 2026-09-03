# Task 2 report: deterministic application bundle

## Delivered

- `scripts/publish-release.ps1`: requires an external, non-existent output root; publishes the Task 1 `win-x64-release` profile into generated staging; rejects forbidden payload files; creates a deterministic App-only ZIP, fixed-order manifest, and `SHA256SUMS`; then moves the completed candidate into place.
- `scripts/verify-release.ps1`: safely returns non-zero on invalid manifest paths or hashes, duplicate paths, forbidden files, archive mismatch, missing executables, role-package contamination, or an invalid framework-dependent `win-x64` contract.
- `tests/release/test_release_manifest.py`: runs the real verifier against synthetic candidates and covers POSIX paths, lowercase SHA-256, duplicates, forbidden names/extensions, missing/uppercase hashes, and `.fgopetpack` separation.

## Commit

- `2f5bd66 build(release): create verified application bundle`

## Test evidence

| Command | Result |
| --- | --- |
| `pytest tests/release/test_release_manifest.py -q` (before implementation) | Failed as expected: test file did not exist. |
| `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q tests/release/test_release_manifest.py` (after implementation) | Passed: `7 passed in 11.31s`. |
| `pwsh -NoProfile -File scripts/test-packaging.ps1` | Publish-profile contract passed; Python packaging tests passed (`71 passed`). The .NET test stage could not run because this worktree lacks `tests/FgoPet.Core.Tests/obj/project.assets.json` and the command uses `--no-restore`. |
| PowerShell parser check for both release scripts | Passed. |

## Concerns

- The existing packaging gate cannot complete its .NET stage until restore assets are supplied for this worktree. No restore was performed to avoid changing dependency state.
- No candidate was published in this task; that isolated external-output acceptance is reserved for the subsequent release-candidate task.

## Fix round 1

### Fixed findings

- The manifest now records `target_framework: net8.0-windows` and `runtime_requirement: .NET 8 Desktop Runtime`; the verifier requires both exact values alongside `win-x64` and framework-dependent publishing.
- Publish and verification now reject sensitive filename patterns and path components for credentials, secrets, pairing state, user data, logs, screenshots, source trees, VCS/IDE state, and development artifacts. The extension checks include database and certificate/key formats.
- The verifier rejects non-canonical POSIX paths containing `.` components as well as traversal, empty, rooted, drive-qualified, and backslash paths.
- Tests now cover absent, empty, and uppercase `sha256` values. The unused `shutil` import was removed.

### Fix validation

| Command | Result |
| --- | --- |
| `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q tests/release/test_release_manifest.py -k forbidden` | Passed: `13 passed, 10 deselected in 21.06s`. |
| `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q tests/release/test_release_manifest.py -k "not forbidden"` | Passed: `10 passed, 13 deselected in 16.21s`. |
| PowerShell parser check for both release scripts | Passed. |

### Remaining concern

- `scripts/test-packaging.ps1` still reaches its existing .NET restore-assets blocker after its publish-profile and Python stages pass; no restore was performed.
