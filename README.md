# FGO Pet

FGO Pet is a Windows 11 desktop companion that presents work, study, Codex progress, and focus sessions through an FGO servant persona.

The production host is a WPF desktop pet with installable `.fgopetpack` servant packs. The first servant is Mash Kyrielight. The program release contains no servant images, prompts, or persona resources: those ship separately as data-only role packs.

## Current status

Phase 1 (`.NET` offline pet host) is implemented across `src/FgoPet.Core`, `src/FgoPet.Infrastructure`, and `src/FgoPet.App`: pack/art contracts, DPI-safe geometry, validated frozen portrait snapshots, packless offline startup, a transactional `.fgopetpack` installer, version/recovery repository, two-phase activation with a bounded snapshot cache, settings + window placement persistence, transparent window hit-testing/drag/DPI hookups, single instance + tray, a servant library, and bounded collapsible attached panels.

Phase 2 is implementation-complete and user-accepted on the primary Release environment. It adds a recoverable local focus timer and per-servant progression on the same panel and portrait contract:

- **Presets.** Built-ins are exactly `25/5 × 4` and `50/10 × 2`. Custom values: focus 5–180 minutes, break 1–60 minutes, 1–12 cycles; invalid values disable start. Built-ins are code constants; the last valid custom preset persists as `custom.last`.
- **Recovery.** Active focus/break sessions recover paused from the latest integer-second snapshot after any restart (normal exit or forced process kill). Offline wall time never advances a session. Snapshots save on every command and at most once per 30 consumed seconds while running.
- **Four header columns.** The attached header is `专注 | 今日 | TODO | 对话`; the columns stretch the same panel (220–340 DIP), the portrait never moves, portrait click closes the panel, and `Esc` steps down. There is no collapse button.
- **Terminal-instrument UI.** The approved Phase 0/1 navy/cyan/magenta visual language now uses a stable grid: horizontal preset comparison, editable stepper fields, calculated duration, timer round/progress metadata, and original geometric footer ornaments.
- **Per-servant bond.** Bond belongs to the servant captured at focus-stage start; servant changes mid-stage do not move the credit. The built-in curve is cumulative 1/3/6/10/15/21/28/36/45 effective hours for levels 2–10, capped at `Lv.10`; achieved levels never decrease.
- **Package dialogue (optional).** Characterized feedback comes only from an installed pack's `dialogue/` resources; packs without them fall back to neutral status text. Malformed dialogue never errors visibly.
- **Runtime store.** Focus sessions, events, timeline, and bond data live in one versioned SQLite database (`runtime.db` under the app's per-user storage root, next to the JSON settings and window placement files). Completion of a focus stage commits session, event, timeline, and bond atomically and idempotently.
- **Phase 3 settings shell and dialogue (accepted on the primary Release environment, 2026-08-30).** All configuration lives in one embedded settings window (个人资料 / 个性化 / 角色包 / AI 模型与连接 / 对话与记忆 / 数据与隐私 / 主题); the tray and portrait menus expose 设置 only. API keys stay in Windows Credential Manager, provider metadata in JSON, conversation and memory records in SQLite. The runtime dialogue panel gained provider/model badges, empty and configuration-required states, and role-styled bubbles; a missing model configuration routes to 设置 > AI 模型与连接 while the pet and focus features still run offline. Broader device coverage remains a release gate: `docs/testing/phase3-settings-matrix.md`.
- **Phase 4 Agent integration (accepted on the primary Windows environment, 2026-09-01; not yet merged or published).** TODO items can be proposed and confirmed, dispatched through a private versioned protocol and local relay, projected into a bounded Todo timeline, and archived into user-confirmed work summaries. The runtime repair adds user-scoped Relay ownership, protected pairing state, per-instance permissions, App/Adapter lifecycle wiring, a Codex App Server worker, and isolated installation tooling. The closeout checks focus on task completion projection and durable delivery across process failures. Exact Codex desktop navigation remains capability-gated with an in-app fallback. Historical evidence: `docs/superpowers/plans/2026-08-31-phase4-final-acceptance.md`; it does not prove acceptance of later changes.
- **Phase 5 direction:** prioritize everyday task/configuration usability, recoverable backups, reproducible servant packs, and a clean install/upgrade path. Application awareness and collectible features are deferred rather than required for the first release. See `docs/superpowers/plans/2026-09-01-phase5-adjusted-direction.md`.
- **Still unavailable:** full backup restore and the final distribution workflow remain future work. LLM/Prompt/memory automation remains limited to explicit user-confirmed flows. Phase 4 closeout and user desktop evidence are recorded in `docs/reports/2026-09-01-phase4-closeout.md`; broader installation/device coverage remains a release gate.

The independent P1.4 packaging SDK (Python) is a separate plan that shares the same pack contract fixtures (`tests/fixtures/packs/`).

Plans and specs live under `docs/superpowers/`. The renderer choice is recorded in `docs/decisions/0001-windows-portrait-renderer.md`. The real-device verification matrices are `docs/testing/phase1-windows-matrix.md`, `docs/testing/phase2-windows-matrix.md`, and `docs/testing/phase3-settings-matrix.md`.

## Build and test

Requirements: .NET SDK 8.0.x, Windows.

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release        # unit/STA + Windows integration (interactive desktop)
pwsh -File scripts/test-phase1.ps1       # full Phase 1 gate
pwsh -File scripts/test-phase2.ps1       # full Phase 2 gate (includes Phase 1)
pwsh -File scripts/test-phase3-settings.ps1  # Phase 3 settings gate (four Release suites)
```

Startup smoke test (no pack needed; verifies the packless state and exits 0):

```powershell
dotnet run --project src/FgoPet.App/FgoPet.App.csproj -c Release -- --smoke-test
```

FGO artwork and extracted Atlas assets are not stored in this repository.
