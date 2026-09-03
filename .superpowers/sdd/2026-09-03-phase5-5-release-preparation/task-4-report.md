# Task 4 report: final release-preparation verification

Date: 2026-09-03
Worktree: `D:\fgo_unpack\fgo_pet\.worktrees\phase5-5-release-prep`

## Status

Blocked on missing NuGet restore assets. No restore was attempted because the prior restore was interrupted and the brief requires bounded work. No release candidate was produced.

## Changed files

- `docs/superpowers/plans/2026-09-03-phase5-closeout.md`
- `.superpowers/sdd/2026-09-03-phase5-5-release-preparation/task-4-report.md`

## Commands and results

- `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q` — incomplete; bounded execution output reached 39% and did not produce a final result.
- `pwsh -NoProfile -File scripts/test-packaging.ps1` — bounded attempt did not obtain a result; the first wrapper invocation rejected identical stdout/stderr redirection. No restore was performed.
- PowerShell AST parser over `scripts/*.ps1` — passed: `PARSED 11 PowerShell scripts`.
- `dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release --no-restore` — failed immediately with `NETSDK1004`; missing `D:\fgo_unpack\fgo_pet\.worktrees\phase5-5-release-prep\src\FgoPet.App\obj\project.assets.json`.
- Release-focused pytest attempt — incomplete; no final result within the bounded execution window.

## Assets and candidate

Inspection found no usable Release publish output, NuGet assets, candidate archive, manifest, or SHA-256 inventory in the worktree. Consequently publish, verify, archive scanning, candidate version/hash recording, and isolated acceptance were not possible.

## Concerns and remaining gaps

The exact blocker is the absent App restore asset file (`src/FgoPet.App/obj/project.assets.json`). A bounded, worktree-local restore may be required before repeating the build and packaging gates. Manual Windows evidence remains absent for GUI install, sleep/resume, DPI, multi-monitor, and long-running operation. Public release readiness must not be claimed. No remote publication, signing, upload, user-state, PATH, Codex configuration, or agent spawning was performed.

## Fix

Updated `scripts/publish-release.ps1` to pass `--no-restore` to the Task 1 profile publish invocation. Added a focused regression test asserting the release publisher uses prepared worktree assets without restoring user NuGet configuration. Existing staging, payload-boundary, manifest, hash, and atomic-output behavior is unchanged.

## Release blocker follow-up

The current release blocker was reproduced in this worktree: `dotnet publish src/FgoPet.App/FgoPet.App.csproj -c Release -r win-x64 --self-contained false --no-restore ...` failed with `NETSDK1047` because `src/FgoPet.App/obj/project.assets.json` had no `net8.0-windows/win-x64` target. The App project did not declare `RuntimeIdentifiers`.

The minimal fix is `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` in `src/FgoPet.App/FgoPet.App.csproj`, matching the release profile's `win-x64` RID and preserving existing behavior.

Verification was limited as requested: the App project XML parsed successfully and `git diff --check` passed. A worktree-local restore using `NuGet.Temp.Config` was attempted but could not read `C:\Users\24139\AppData\Roaming\NuGet\NuGet.Config` due to access denial; network restore was then stopped. No publish or tests were run after the interruption.
