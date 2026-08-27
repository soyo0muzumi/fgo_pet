# FGO Pet

FGO Pet is a Windows 11 desktop companion that presents work, study, Codex progress, and focus sessions through an FGO servant persona.

The production host is a WPF desktop pet with installable `.fgopetpack` servant packs. The first servant is Mash Kyrielight. The program release contains no servant images, prompts, or persona resources: those ship separately as data-only role packs.

## Current status

Phase 1 (`.NET` offline pet host) is implemented across `src/FgoPet.Core`, `src/FgoPet.Infrastructure`, and `src/FgoPet.App`: pack/art contracts, DPI-safe geometry, validated frozen portrait snapshots, packless offline startup, a transactional `.fgopetpack` installer, version/recovery repository, two-phase activation with a bounded snapshot cache, settings + window placement persistence, transparent window hit-testing/drag/DPI hookups, single instance + tray, a servant library, and bounded collapsible attached panels.

The independent P1.4 packaging SDK (Python) is a separate plan that shares the same pack contract fixtures (`tests/fixtures/packs/`).

Plans and specs live under `docs/superpowers/`. The renderer choice is recorded in `docs/decisions/0001-windows-portrait-renderer.md`. The real-device verification matrix is `docs/testing/phase1-windows-matrix.md`.

## Build and test

Requirements: .NET SDK 8.0.x, Windows.

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release        # unit/STA + Windows integration (interactive desktop)
pwsh -File scripts/test-phase1.ps1       # full Phase 1 gate
```

Startup smoke test (no pack needed; verifies the packless state and exits 0):

```powershell
dotnet run --project src/FgoPet.App/FgoPet.App.csproj -c Release -- --smoke-test
```

FGO artwork and extracted Atlas assets are not stored in this repository.