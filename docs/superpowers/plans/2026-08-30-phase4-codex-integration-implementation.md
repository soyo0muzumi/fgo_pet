# Phase 4 Codex Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional, privacy-preserving Codex task integration layer that can receive sanitized task events, persist and deliver them reliably, enrich dialogue only when enabled, and provide task jumping without changing Phase 3 behavior.

**Architecture:** Keep the existing Phase 2/3 `RuntimeEvent`, SQLite, settings, dialogue orchestration, and tray contracts as the compatibility base. Add a versioned external-event contract and adapter at the boundary, persist incoming events in a durable local outbox with idempotent task/sequence keys, and expose optional task context to `PromptComposer` only after sanitization. The Codex Skill/MCP sender remains independent of the desktop app; when the bridge is absent or disabled, the app behaves exactly as it does today.

**Tech Stack:** .NET 8, C# records/interfaces, WPF, Microsoft.Data.Sqlite, xUnit 2.5.3, Windows Credential Manager (existing Phase 3 provider storage).

**Spec:** `docs/superpowers/specs/2026-08-25-fgo-pet-design.md` sections 6–7, 12.3, 13–16; `docs/superpowers/specs/2026-08-29-phase3-dialogue-memory-design.md` section 15.

## Global Constraints

- Codex events contain only task id, title, status, time, user-readable summary, result entry point, and privacy marker; commands, tool calls, full reasoning, terminal output, API keys, and local paths never enter the desktop database.
- Sensitive events are anonymized before persistence; filtering happens before prompt composition and before logs.
- Phase 3 remains fully usable when no bridge, plugin, transport, or model is configured.
- Event delivery is idempotent by `(task_id, sequence)` and preserves sender order after reconnect.
- No external event automatically invokes the model; dialogue remains user initiated.
- Existing `servant_id` ownership, Phase 2 focus/timeline/bond semantics, and Phase 3 conversation/memory semantics remain unchanged.
- API keys continue to live only in Windows Credential Manager and never enter event payloads, SQLite, exports, or diagnostics.
- Phase 4 UI must not advertise Phase 5 backup/restore, application awareness, installation packages, or other unavailable capabilities.

---

### Task 1: Version the unified event contract and storage migration

**Files:**
- Create: `src/FgoPet.Core/Events/RuntimeEventSource.cs`
- Modify: `src/FgoPet.Core/Events/RuntimeEvent.cs`
- Modify: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs`
- Modify: `src/FgoPet.Infrastructure/Events/SqliteEventStore.cs`
- Test: `tests/FgoPet.Core.Tests/Events/RuntimeEventContractTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Events/SqliteEventStoreTests.cs`

**Interfaces:**
- Consumes: Existing `RuntimeEvent` constructors and `SqliteEventStore.TryInsert` callers.
- Produces: Optional `RuntimeEvent.Source`, `RuntimeEvent.SubjectId`, `RuntimeEvent.Summary`, and `RuntimeEvent.IsPrivate` fields with backward-compatible defaults; schema migration `4` that adds the same columns with safe defaults.

- [ ] **Step 1: Write failing contract tests**

  Assert that a legacy focus event defaults to `source = "system"`, has no subject or summary, and is not private. Assert that a Codex event can carry `source = "codex"`, a subject id, a sanitized summary, and a private marker without changing the existing positional constructor call sites.

- [ ] **Step 2: Run the focused tests to verify they fail**

  Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RuntimeEventContractTests"`

  Expected: FAIL because the optional source metadata does not exist yet.

- [ ] **Step 3: Add the optional fields and migration**

  Append the optional fields after `PayloadJson` so all Phase 2/3 call sites remain source-compatible. Add migration 4 with `source TEXT NOT NULL DEFAULT 'system'`, nullable `subject_id` and `summary`, and `is_private INTEGER NOT NULL DEFAULT 0 CHECK(is_private IN (0,1))`. Update `SqliteEventStore` inserts and add a read-back helper only if the existing repository pattern requires it; do not rewrite focus projections.

- [ ] **Step 4: Run contract and persistence tests**

  Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RuntimeEventContractTests"` and `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteEventStoreTests"`

  Expected: PASS, including migration from a version-3 fixture and idempotent insertion of an event with the new metadata.

- [ ] **Step 5: Commit**

  `git add src/FgoPet.Core/Events src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs src/FgoPet.Infrastructure/Events/SqliteEventStore.cs tests/FgoPet.Core.Tests/Events tests/FgoPet.Infrastructure.Tests/Events`  
  `git commit -m "feat: version unified runtime event metadata"`

### Task 2: Define and sanitize the Codex task event boundary

**Files:**
- Create: `src/FgoPet.Core/Events/CodexTaskEvent.cs`
- Create: `src/FgoPet.Core/Events/CodexTaskEventType.cs`
- Create: `src/FgoPet.App/Integration/CodexEventSanitizer.cs`
- Create: `src/FgoPet.App/Integration/CodexEventProjector.cs`
- Test: `tests/FgoPet.Core.Tests/Events/CodexTaskEventContractTests.cs`
- Test: `tests/FgoPet.App.Tests/Integration/CodexEventSanitizerTests.cs`

**Interfaces:**
- Consumes: `CodexTaskEvent` with `TaskId`, `Sequence`, `Type`, `OccurredAtUtc`, optional `Title`, `Summary`, `ResultUri`, and `IsSensitive`.
- Produces: `SanitizedCodexEvent` and `RuntimeEvent CodexEventProjector.Project(SanitizedCodexEvent)`, rejecting unsupported types and returning an explicit reason rather than throwing into the tray/UI loop.

- [ ] **Step 1: Write failing sanitizer tests** for supported event types, title/summary length limits, sensitive anonymization, URI allow-listing, removal of path-like and credential-like text, priority mapping, and duplicate sequence acceptance for later queue handling.
- [ ] **Step 2: Run `dotnet test ... --filter "FullyQualifiedName~CodexEventSanitizerTests"`** and observe the expected missing-type failures.
- [ ] **Step 3: Implement deterministic allow-list sanitization** with no network or filesystem reads. Sensitive events produce anonymous status text and null title/summary/result URI. Non-sensitive summaries are normalized to bounded plain text; `http(s)` result URIs are retained only when they contain no credentials, query secrets, or local path markers.
- [ ] **Step 4: Project sanitized events** into the versioned runtime contract with `Source = "codex"`, `SubjectId = TaskId`, `IsPrivate = IsSensitive`, and a stable event id derived from task id and sequence when the sender did not provide one.
- [ ] **Step 5: Run focused tests and commit** with `feat: add sanitized Codex event boundary`.

### Task 3: Add the local durable event bridge and idempotent outbox

**Files:**
- Create: `src/FgoPet.Core/Integration/IExternalEventSink.cs`
- Create: `src/FgoPet.Infrastructure/Integration/SqliteExternalEventOutbox.cs`
- Modify: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs` (migration 5)
- Create: `src/FgoPet.App/Integration/CodexEventBridge.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Integration/SqliteExternalEventOutboxTests.cs`
- Test: `tests/FgoPet.App.Tests/Integration/CodexEventBridgeTests.cs`

**Interfaces:**
- Consumes: `SanitizedCodexEvent` from Task 2.
- Produces: `IExternalEventSink.AcceptAsync(SanitizedCodexEvent, CancellationToken)`, `GetPendingAsync(int, CancellationToken)`, `MarkDeliveredAsync(string, CancellationToken)`, and a SQLite outbox unique key on `(task_id, sequence)`.

- [ ] **Step 1: Write failing tests** for offline enqueue, duplicate task/sequence no-op, FIFO retrieval, retry state retention, and privacy-preserving serialized payloads.
- [ ] **Step 2: Run the focused infrastructure/app tests** and verify the outbox and bridge are absent.
- [ ] **Step 3: Add migration 5** for `external_event_outbox` with status, attempt count, next-attempt timestamp, sanitized JSON, and a unique `(task_id, sequence)` index; keep raw inbound JSON out of the table.
- [ ] **Step 4: Implement the bridge** so a transport failure leaves the row pending and never blocks Phase 3 startup or user-initiated dialogue.
- [ ] **Step 5: Run focused tests and commit** with `feat: persist sanitized external events locally`.

### Task 4: Implement reconnect, ordering, and transport adapters

**Files:**
- Create: `src/FgoPet.Core/Integration/IExternalEventTransport.cs`
- Create: `src/FgoPet.App/Integration/ExternalEventDispatcher.cs`
- Create: `src/FgoPet.App/Integration/BackoffPolicy.cs`
- Test: `tests/FgoPet.App.Tests/Integration/ExternalEventDispatcherTests.cs`

**Interfaces:**
- Consumes: Pending outbox rows from Task 3 and an `IExternalEventTransport.SendAsync(SanitizedCodexEvent, CancellationToken)` implementation supplied by the local bridge/MCP adapter.
- Produces: Ordered drain with bounded exponential backoff, cancellation, and no duplicate delivery after acknowledged rows.

- [ ] **Step 1: Write failing tests** for FIFO delivery, reconnect after transient failure, cancellation, max-attempt quarantine, and duplicate acknowledgement.
- [ ] **Step 2: Implement the dispatcher** with one active drain per bridge, monotonic task sequence checks, and bounded retry delays from a pure `BackoffPolicy`.
- [ ] **Step 3: Keep transport optional** in DI; absent transport means pending rows remain local and Phase 3 remains available.
- [ ] **Step 4: Run tests and commit** with `feat: drain external event outbox after reconnect`.

### Task 5: Add optional sanitized task context to dialogue

**Files:**
- Create: `src/FgoPet.Core/Dialogue/ExternalTaskContext.cs`
- Create: `src/FgoPet.App/Integration/IExternalTaskContextProvider.cs`
- Modify: `src/FgoPet.App/Dialogue/ConversationOrchestrator.cs`
- Modify: `src/FgoPet.App/Dialogue/PromptComposer.cs`
- Test: `tests/FgoPet.App.Tests/Dialogue/PromptComposerTests.cs`
- Test: `tests/FgoPet.App.Tests/Dialogue/ConversationOrchestratorTests.cs`

**Interfaces:**
- Consumes: `IExternalTaskContextProvider.TryGetAsync(string servantId, CancellationToken)` returning a sanitized `ExternalTaskContext?` only when the user enables task context.
- Produces: A bounded `external_task_context` prompt layer that contains status, short summary, and result label only; no automatic model call is introduced.

- [ ] **Step 1: Write failing prompt tests** proving disabled/absent context changes no existing prompt, enabled context is bounded and labeled, sensitive context is anonymous, and task context cannot override safety/system layers.
- [ ] **Step 2: Add the optional provider dependency** to `ConversationOrchestrator` without changing required constructor behavior for existing tests.
- [ ] **Step 3: Compose context through the existing runtime-state budget** and retain prompt-injection wrapping and truncation.
- [ ] **Step 4: Run the complete App test project and commit** with `feat: add optional Codex task context to dialogue`.

### Task 6: Add privacy settings and task-jump affordances

**Files:**
- Modify: `src/FgoPet.Core/Settings/AppSettings.cs` (or the existing settings contract containing privacy preferences)
- Modify: `src/FgoPet.App/Settings/DataPrivacyPage.xaml` and code-behind
- Create: `src/FgoPet.App/Integration/ITaskJumpHandler.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Test: `tests/FgoPet.App.Tests/Settings/DataPrivacyPageViewModelTests.cs`
- Test: `tests/FgoPet.Windows.Tests/Panels/AttachedPanelViewIntegrationTests.cs`

**Interfaces:**
- Consumes: User-controlled integration enablement, sensitive-event policy, and task filter values; sanitized `SubjectId`/result entry point from the event projection.
- Produces: A disabled-by-default or explicitly opt-in integration control, an accessible “打开任务” action, and a copy-id fallback when no jump handler is available.

- [ ] **Step 1: Write failing settings/UI tests** for opt-in persistence, clear privacy copy, disabled bridge behavior, keyboard activation, and fallback copy action.
- [ ] **Step 2: Implement settings and command routing** through the existing embedded settings shell; do not add a new top-level window.
- [ ] **Step 3: Implement `ITaskJumpHandler.TryOpenAsync`** with no-op/fallback behavior when Codex is unavailable.
- [ ] **Step 4: Run App and Windows tests, perform a manual compact-panel review, and commit** with `feat: add optional Codex integration controls`.

### Task 7: Add Skill/MCP protocol manifests and release gate

**Files:**
- Create: `integrations/codex/skill/README.md`
- Create: `integrations/codex/skill/event-schema.json`
- Create: `integrations/codex/mcp/README.md`
- Create: `docs/testing/phase4-codex-integration-matrix.md`
- Create: `scripts/test-phase4.ps1`
- Modify: `docs/reports/2026-08-30-phase3-release-and-phase4-handoff.md`

**Interfaces:**
- Consumes: Sanitized event schema and bridge endpoint from Tasks 2–4.
- Produces: Versioned sender documentation, schema examples that exclude forbidden fields, and a serial Release gate covering migration, privacy, queue, reconnect, prompt, and UI fallback behavior.

- [ ] **Step 1: Add schema fixtures** for every supported Codex task event and all forbidden-field rejection cases.
- [ ] **Step 2: Document protocol version negotiation** and sender-side persistence/retry responsibilities without embedding provider credentials.
- [ ] **Step 3: Implement the Release gate** as serial project tests plus secret/path scans and a manual matrix for 100%/150%/200% DPI.
- [ ] **Step 4: Run the gate, update the handoff report, and commit** with `docs: add Phase 4 Codex integration gate`.

## Verification Gate

Before declaring Phase 4 complete, run from `D:\fgo_unpack\fgo_pet`:

```powershell
dotnet test FgoPet.sln -c Release --no-restore
dotnet build FgoPet.sln -c Release --no-restore -warnaserror
pwsh -NoProfile -File scripts/test-phase4.ps1
git diff --check
```

The manual matrix must confirm bridge-disabled startup, sensitive-event anonymization, duplicate/reordered delivery, reconnect, task-jump fallback, and existing Phase 3 settings/dialogue behavior at 100%, 150%, and 200% scaling. A missing Codex plugin or transport is a supported offline state, not a release failure.
