# Phase 4 final acceptance — 2026-08-31

Status: automated checks and real-machine transport acceptance completed; final App-side projection acceptance remains open for one fresh post-fix UI dispatch.

## Automated evidence

- `dotnet restore FgoPet.sln`: passed in normal Windows user context.
- Release solution build with `-warnaserror`: passed, zero warnings/errors.
- Final full Release test run: 671 tests, 671 passed across all nine test projects.
- Updated that assertion to check real assembly references and the approved Runtime dependency; affected architecture subset: 7/7 passed. No unrelated full-suite rerun.
- New focused checks include durable dispatch dedupe, authorization cancellation, RPC initialization/streaming/approval denial, explicit target registration, per-instance dispatch UI, and local cancellation after successful revoke.
- Isolated `scripts/test-phase4.ps1 -SkipBuild -PublishedSource ./src/FgoPet.App/bin/Release/net8.0-windows`: plugin validation, installation, installed MCP initialize/tools-list, and scoped uninstall passed. User PATH and normal Codex plugin configuration were not changed.
- Release no-build publish contains both companion exe/dll/deps/runtimeconfig sets and `System.Security.Cryptography.ProtectedData.dll`.

## Actual Windows evidence

- Launched Release App with `FGO_PET_PIPE_SUFFIX=acceptance-stage-c-0831` and an isolated `.superpowers/visible-acceptance-state` root.
- Used Windows UI Automation against the specific launched process to inspect and invoke Agent settings controls.
- Found and corrected a deployment-only failure: WPF shared-framework resolution omitted ProtectedData from App output, but the standalone companions require that DLL beside them. Rebuilt App with zero warnings/errors; companion now remains running.
- App, Relay and Adapter subsequently reported online. Invoked the visible Test Connection control and observed all three still online.
- Observed one matching Relay process (PID 43816) and an Adapter worker (PID 43348) at the time of inspection.
- An approved Codex source appeared in the isolated UI; root did not invoke the approval button, so approval provenance is awaiting user confirmation. Do not attribute this approval step to automation.
- Screenshot: `.superpowers/agent-connected.png` (scrolled source card); the all-online status is verified separately by UI Automation text.
- Found and fixed an additional real restart issue: JsonAppSettingsStore omitted AgentConnection. Real-store settings subset passed 17/17; App rebuilt with zero warnings/errors. Saved enabled=true through the visible UI, verified the JSON flag, terminated the isolated process tree and restarted: the same source instance reconnected without reapproval, all three endpoints were online, and exactly one matching Relay remained (PID 43228 at inspection).
- Invoked the visible Revoke Authorization control for that isolated source: the approved list became empty, Adapter showed offline and its worker exited. App/Relay remained online. The isolated App tree was then stopped; state/evidence retained, no production data removed.
- Relaunched the isolated Release App with the UI window actually present, then exercised three real Todo dispatches through the visible App flow. The Adapter started real Codex app-server threads `01a0573f-372b-72f3-80b9-151244c6cb4e`, `01a0574a-ab4a-7182-b541-6d73eaf305e9`, and `01a0574e-9e60-7612-8e79-505e4592f739`; each emitted `task_started`, `task_updated`, and `task_completed` receipts in the isolated runtime database.
- Those three dispatches were created before the reservation-ordering fix and their App projections remain `dispatching`/`active`; they are retained as evidence of the previously observed race, not counted as final acceptance. The fix is covered by the affected `AgentDispatchServiceTests` subset (5/5) and the Release build, but a new UI-dispatched Todo must still be observed ending in `completed` before Phase 4 is marked fully accepted.

## Not yet proven / permissions

- Fresh post-fix App projection (`agent_executions` and Todo status ending in `completed`) has not yet been proven. The current isolated App window did not expose a stable Todo panel after restart, so no claim of full Phase 4 acceptance is made.
- Visible restart and revoke are verified. In-flight real model cancellation and actual dispatch/progress/completion still require the account-backed task acceptance.
- Production user PATH/plugin installation was deliberately not performed; isolated plugin registration was verified by the packaging worker.
- No merge, commit, release upload or changes to the original dirty checkout were performed.

Only affected checks are repeated after identified fixes. The retained test files remain useful regression coverage; previously passing tests are not permanently exempt from future relevant changes.
