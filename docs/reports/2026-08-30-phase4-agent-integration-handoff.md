# Phase 4 Agent integration handoff

Date: 2026-08-30

## Delivered

Phase 4 is implemented across Core, Infrastructure, App, AgentProtocol, AgentRelay,
CodexAdapter, and the local plugin package. The implementation includes:

- generic Todo, Agent execution, event, connection, capability, and work-archive contracts;
- SQLite persistence and migration for Todos, executions, receipts, connections,
  targets, archives, and archive coverage;
- private versioned wire protocol with validation, denylisted fields, redaction,
  source pairing, relay routing, deduplication, offline handling, and revoke;
- App gateway, event projection, reconnect recovery, Todo UI, dispatch confirmation,
  attention/current-task strip, archive confirmation, export, and clear-data flows;
- Codex App Server JSON-RPC boundary, deterministic hook mapper, confirmation-gated
  MCP completion tools, relay session, plugin manifest, hooks, and skill;
- automated end-to-end coverage for confirmed MCP completion, relay idempotency and
  redaction, and offline/retry dispatch behavior.

## Verification

The implementation was developed in small commits with targeted tests after each
boundary. The final machine gate is:

```powershell
dotnet restore FgoPet.sln
dotnet build FgoPet.sln --no-restore
dotnet test FgoPet.sln --no-build
```

Release verification passed with 0 warnings, 0 errors, and 569/569 tests passing
across all 8 test assemblies. The Codex plugin manifest validator also passes for
`integrations/codex/fgo-pet-agent`.

## Manual Windows gate still required

The following require a real desktop installation and are intentionally not claimed
by unit tests:

1. Relay single-instance behavior and Windows named-pipe ACL inspection.
2. Plugin install/revoke in the target Codex runtime.
3. App restart recovery with a live adapter and settings switch.
4. Top navigation visual regression, scrollable Todo layout, reduced-motion behavior,
   and the Agent acceptance loop.

## Known limitations and future adapter points

- The relay registration/grant store is currently process-local; production
  installation needs durable protected credential storage and explicit named-pipe ACL
  verification.
- The App Server transport is injected and tested at the JSON-RPC boundary; the
  exact Codex desktop runtime handshake must be validated on the supported build.
- Exact desktop task navigation is not guaranteed. It is capability-gated and the app
  uses an in-app fallback until a runtime proves support; Phase 4 does not promise a
  guessed `codex://` scheme.
- Agent connection enable/disable and pending-data clear controls are persisted in the
  app model and sent through the versioned App pipe; durable relay-process grant
  storage and multi-process lifecycle wiring remain deployment work.
- Work-archive metadata and long-archive summaries are persisted and exported only
  after explicit user confirmation. No background compression or silent memory write
  is performed.

## Commit range

The Phase 4 implementation commits are:

`7afe8a4`, `f139ac3`, `d9ae2ba`, `debd484`, `e6418a1`, `74606ec`, `eada3e6`,
`38ec69c`, `5fc4a76`, `2b03f93`, `9cb60bc`, `5b45465`, `623109e`, `68b3ff0`,
`275023c`, `481a36d`, `e63afa9`, `b8540f8`, and `21b1efb`.
