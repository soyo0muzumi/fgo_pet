# Phase 2 Events, Focus, and Timeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Phase 1 WPF desktop pet with a recoverable local focus timer, minimal event timeline, per-servant bond levels, and package-provided event dialogue without changing the approved portrait or attached-panel contract.

**Architecture:** Add pure focus/event/bond models to `FgoPet.Core`, a single versioned SQLite runtime store to `FgoPet.Infrastructure`, and orchestration plus additive attached-panel states to `FgoPet.App`. The focus-completion transaction is the consistency boundary: session state, runtime event, timeline projection, effective seconds, bond ledger, and optional level-up commit together.

**Tech Stack:** C# 12, .NET 8, WPF, CommunityToolkit.Mvvm 8.3.2, Microsoft.Data.Sqlite 8.0.1, xUnit 2.5.3.

**Spec:** `docs/superpowers/specs/2026-08-28-phase2-events-focus-timeline-design.md`

## Global Constraints

- Phase 2 is an additive extension of the Phase 1 `main` implementation; do not create a second panel, timer window, portrait host, or package runtime.
- Preserve the Phase 0/1 dark-navy translucent panel, cyan border, cyan/magenta accents, clipped corners, `220–340 DIP` width, manifest anchor, portrait stability, drag/hit behavior, and 60% work-area height cap.
- The attached header is exactly `专注 | 今日 | TODO | 对话`; there is no visible collapse action. Portrait click closes the panel and `Esc` steps down.
- The built-in presets are exactly `25/5 × 4` and `50/10 × 2`; custom values are focus `5–180` minutes, break `1–60` minutes, and `1–12` cycles.
- `TODO` remains an empty future integration surface. Do not add a Todo model, table, fixture, editor, or persistence API.
- Active focus/break sessions recover paused from the latest reliable integer-second snapshot. Offline wall time never advances a session.
- Runtime business data uses one versioned SQLite database; existing settings and window-placement JSON remain in place.
- Store UTC timestamps, integer seconds, `0/1` booleans, stable lowercase enum strings, and stable string business IDs.
- Bond belongs to the servant captured at focus-stage start. Appearance changes do not change ownership; servant changes during a stage do not move the credit.
- The built-in progression curve is cumulative 1/3/6/10/15/21/28/36/45 effective hours for levels 2–10, capped at `Lv.10`. Achieved levels never decrease.
- Characterized feedback comes only from optional package `dialogue/` resources. The application fallback is neutral status text.
- Do not implement LLM, Prompt, memory, lore retrieval, Codex/Agent bridge, app awareness, collectibles, graphs, search, exports, or release packaging.
- Follow TDD: observe each targeted test fail for the intended missing behavior before adding production code.
- Keep every existing Phase 1 test green after every task; do not rewrite unrelated Phase 1 code.

---

## File Structure

### Core additions

- `src/FgoPet.Core/Focus/FocusPreset.cs` — preset bounds and total-duration calculation.
- `src/FgoPet.Core/Focus/FocusSession.cs` — immutable session snapshot and stable state strings.
- `src/FgoPet.Core/Focus/FocusCommand.cs` — allowed user/time commands.
- `src/FgoPet.Core/Focus/FocusTransition.cs` — transition result plus emitted domain events.
- `src/FgoPet.Core/Focus/FocusStateMachine.cs` — pure deterministic transition engine.
- `src/FgoPet.Core/Events/RuntimeEvent.cs` — stable event contract.
- `src/FgoPet.Core/Timeline/TimelineEntry.cs` — minimal read-only projection model.
- `src/FgoPet.Core/Bond/IBondProgressionPolicy.cs` — replaceable internal progression contract.
- `src/FgoPet.Core/Bond/DefaultBondProgressionPolicy.cs` — approved 1–45 hour curve.
- `src/FgoPet.Core/Bond/BondProgress.cs` — evaluated level/progress result.
- `src/FgoPet.Core/Packs/DialogueContract.cs` — safe package dialogue models consumed by Infrastructure and App.

### Infrastructure additions

- `src/FgoPet.Infrastructure/Persistence/RuntimeDatabase.cs` — connection creation and PRAGMA setup.
- `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs` — ordered schema migrations.
- `src/FgoPet.Infrastructure/Focus/SqliteFocusRepository.cs` — presets, session snapshots, and recovery reads.
- `src/FgoPet.Infrastructure/Events/SqliteEventStore.cs` — event persistence and idempotent inserts.
- `src/FgoPet.Infrastructure/Timeline/SqliteTimelineRepository.cs` — today query and projection writes.
- `src/FgoPet.Infrastructure/Bond/SqliteBondRepository.cs` — servant totals, level floor, and unique ledger.
- `src/FgoPet.Infrastructure/Focus/SqliteFocusCompletionUnit.cs` — single completion transaction.
- `src/FgoPet.Infrastructure/Packs/DialogueManifestReader.cs` — strict optional `dialogue/` parsing and safe fallback result.

### App additions/modifications

- `src/FgoPet.App/Focus/FocusSessionService.cs` — TimeProvider-driven orchestration and snapshot cadence.
- `src/FgoPet.App/Focus/IFocusSessionService.cs` — commands and observable snapshot contract.
- `src/FgoPet.App/Feedback/EventFeedbackSelector.cs` — package candidate selection, recent dedupe, neutral fallback.
- `src/FgoPet.App/Panels/AttachedPanelViewModel.cs` — real focus/today state and four header commands.
- `src/FgoPet.App/Panels/AttachedPanelView.xaml` — additive compact/expanded content in the approved visual shell.
- `src/FgoPet.App/Panels/AttachedPanelView.xaml.cs` — visibility switching only; no business logic.
- `src/FgoPet.Core/Panels/AttachedPanelState.cs` — `ExpandedFocus` and `ExpandedToday` states/actions.
- `src/FgoPet.Core/Panels/AttachedPanelStateMachine.cs` — four-column stretch transitions and portrait close.
- `src/FgoPet.App/Main/PortraitWindow.xaml.cs` — preserve geometry while recognizing all expanded states.
- `src/FgoPet.App/Bootstrap/AppPaths.cs` — runtime database location.
- `src/FgoPet.App/Bootstrap/ServiceRegistration.cs` — compose the new services.
- `src/FgoPet.App/Bootstrap/DesktopAppShell.cs` — migrate database and restore paused session during startup.

### Tests and fixtures

- `tests/FgoPet.Core.Tests/Focus/FocusPresetTests.cs`
- `tests/FgoPet.Core.Tests/Focus/FocusStateMachineTests.cs`
- `tests/FgoPet.Core.Tests/Bond/DefaultBondProgressionPolicyTests.cs`
- `tests/FgoPet.Infrastructure.Tests/Persistence/RuntimeDatabaseTests.cs`
- `tests/FgoPet.Infrastructure.Tests/Focus/SqliteFocusCompletionUnitTests.cs`
- `tests/FgoPet.Infrastructure.Tests/Packs/DialogueManifestReaderTests.cs`
- `tests/FgoPet.App.Tests/Focus/FocusSessionServiceTests.cs`
- `tests/FgoPet.App.Tests/Feedback/EventFeedbackSelectorTests.cs`
- `tests/FgoPet.App.Tests/Panels/AttachedPanelViewModelTests.cs`
- `tests/FgoPet.Windows.Tests/Panels/AttachedPanelViewIntegrationTests.cs`
- `tests/FgoPet.Windows.Tests/Windowing/PortraitWindowIntegrationTests.cs`
- `tests/fixtures/packs/dialogue-valid/`
- `tests/fixtures/packs/dialogue-invalid-expression/`

---

### Task 1: Pure Focus Presets and State Machine

**Files:**
- Create: `src/FgoPet.Core/Focus/FocusPreset.cs`
- Create: `src/FgoPet.Core/Focus/FocusSession.cs`
- Create: `src/FgoPet.Core/Focus/FocusCommand.cs`
- Create: `src/FgoPet.Core/Focus/FocusTransition.cs`
- Create: `src/FgoPet.Core/Focus/FocusStateMachine.cs`
- Create: `tests/FgoPet.Core.Tests/Focus/FocusPresetTests.cs`
- Create: `tests/FgoPet.Core.Tests/Focus/FocusStateMachineTests.cs`

**Interfaces:**
- Consumes: `TimeProvider` only at the App boundary; Core receives explicit `DateTimeOffset`/elapsed seconds.
- Produces: `FocusPreset.Create(int focusMinutes, int breakMinutes, int cycles)`, `FocusStateMachine.Apply(FocusSession, FocusCommand, DateTimeOffset)`, and stable event drafts consumed by Tasks 2–4.

- [ ] **Step 1: Write failing preset validation and duration tests**

```csharp
[Theory]
[InlineData(4, 5, 4)]
[InlineData(181, 5, 4)]
[InlineData(25, 0, 4)]
[InlineData(25, 61, 4)]
[InlineData(25, 5, 0)]
[InlineData(25, 5, 13)]
public void Create_rejects_values_outside_the_approved_bounds(int focus, int rest, int cycles) =>
    Assert.Throws<ArgumentOutOfRangeException>(() => FocusPreset.Create(focus, rest, cycles));

[Fact]
public void Total_seconds_excludes_the_break_after_the_last_cycle()
{
    var preset = FocusPreset.Create(35, 10, 3);
    Assert.Equal(7_500, preset.TotalSeconds);
}
```

- [ ] **Step 2: Run the preset tests and verify the missing-type failure**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~FocusPresetTests`

Expected: FAIL with `CS0246` for `FocusPreset`.

- [ ] **Step 3: Implement the bounded preset record**

```csharp
public sealed record FocusPreset(int FocusSeconds, int BreakSeconds, int Cycles)
{
    public int TotalSeconds => checked(FocusSeconds * Cycles + BreakSeconds * (Cycles - 1));

    public static FocusPreset Create(int focusMinutes, int breakMinutes, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(focusMinutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(focusMinutes, 180);
        ArgumentOutOfRangeException.ThrowIfLessThan(breakMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(breakMinutes, 60);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cycles, 12);
        return new(checked(focusMinutes * 60), checked(breakMinutes * 60), cycles);
    }
}
```

- [ ] **Step 4: Write failing state-machine tests for start, pause, resume, completion, stop, and cycle completion**

```csharp
[Fact]
public void Focus_completion_emits_one_event_and_starts_break()
{
    var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
    var result = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(1_500), At.AddMinutes(25));

    Assert.Equal(FocusStatus.Breaking, result.Session.Status);
    Assert.Equal(300, result.Session.RemainingSeconds);
    var completed = Assert.Single(result.Events);
    Assert.Equal(RuntimeEventType.FocusCompleted, completed.Type);
    Assert.Equal("servant-mash", completed.ServantId);
    Assert.Equal(1_500, completed.EffectiveSeconds);
}

[Fact]
public void Stop_during_focus_records_elapsed_but_no_effective_seconds()
{
    var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
    var progressed = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(720), At.AddMinutes(12)).Session;
    var stopped = FocusStateMachine.Apply(progressed, new FocusCommand.Stop(), At.AddMinutes(12));

    Assert.Equal(FocusStatus.Idle, stopped.Session.Status);
    Assert.Equal(0, Assert.Single(stopped.Events).EffectiveSeconds);
    Assert.Equal(720, stopped.Events[0].ElapsedSeconds);
}
```

- [ ] **Step 5: Run the state-machine tests and verify they fail for missing behavior**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~FocusStateMachineTests`

Expected: FAIL because `FocusSession`, `FocusCommand`, and `FocusStateMachine` do not exist.

- [ ] **Step 6: Implement immutable focus state and exhaustive transitions**

Use stable values `idle`, `focusing`, `paused_focus`, `breaking`, `paused_break`, and `completed`. `Start` captures `ServantId`; no later command changes it. `Elapsed` subtracts from `RemainingSeconds`, emits exactly one boundary event, and never processes more than one boundary in one call. `RestorePaused()` maps active states to their paused counterpart without subtracting wall time.

```csharp
public static FocusTransition Apply(FocusSession session, FocusCommand command, DateTimeOffset occurredAtUtc) =>
    (session.Status, command) switch
    {
        (FocusStatus.Idle, FocusCommand.Start start) => Start(session, start, occurredAtUtc),
        (FocusStatus.Focusing, FocusCommand.Pause _) => PauseFocus(session, occurredAtUtc),
        (FocusStatus.PausedFocus, FocusCommand.Resume _) => ResumeFocus(session, occurredAtUtc),
        (FocusStatus.Breaking, FocusCommand.Pause _) => PauseBreak(session, occurredAtUtc),
        (FocusStatus.PausedBreak, FocusCommand.Resume _) => ResumeBreak(session, occurredAtUtc),
        (FocusStatus.Focusing or FocusStatus.Breaking or FocusStatus.PausedFocus or FocusStatus.PausedBreak,
            FocusCommand.Stop _) => Stop(session, occurredAtUtc),
        (FocusStatus.Focusing or FocusStatus.Breaking, FocusCommand.Elapsed elapsed) =>
            Advance(session, elapsed.Seconds, occurredAtUtc),
        (FocusStatus.Completed, FocusCommand.Acknowledge _) => FocusTransition.WithoutEvents(FocusSession.Idle),
        _ => throw new InvalidOperationException($"Command {command.GetType().Name} is invalid for {session.Status}.")
    };
```

- [ ] **Step 7: Run all Core focus tests**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~Focus`

Expected: PASS.

- [ ] **Step 8: Commit Task 1**

```powershell
git add src/FgoPet.Core/Focus tests/FgoPet.Core.Tests/Focus
git commit -m "feat: add focus timer state machine"
```

---

### Task 2: Runtime Events and Per-Servant Bond Progression

**Files:**
- Create: `src/FgoPet.Core/Events/RuntimeEvent.cs`
- Create: `src/FgoPet.Core/Timeline/TimelineEntry.cs`
- Create: `src/FgoPet.Core/Bond/BondProgress.cs`
- Create: `src/FgoPet.Core/Bond/IBondProgressionPolicy.cs`
- Create: `src/FgoPet.Core/Bond/DefaultBondProgressionPolicy.cs`
- Create: `tests/FgoPet.Core.Tests/Bond/DefaultBondProgressionPolicyTests.cs`
- Modify: `src/FgoPet.Core/Focus/FocusTransition.cs`
- Modify: `tests/FgoPet.Core.Tests/Focus/FocusStateMachineTests.cs`

**Interfaces:**
- Consumes: Task 1 transition drafts.
- Produces: `RuntimeEvent`, `RuntimeEventType`, `TimelineEntry`, and `IBondProgressionPolicy.Evaluate(long, int)` for persistence and UI.

- [ ] **Step 1: Write failing level-boundary and non-regression tests**

```csharp
[Theory]
[InlineData(0, 1)]
[InlineData(3_599, 1)]
[InlineData(3_600, 2)]
[InlineData(10_800, 3)]
[InlineData(162_000, 10)]
public void Evaluate_uses_the_approved_cumulative_curve(long seconds, int expectedLevel)
{
    var result = new DefaultBondProgressionPolicy().Evaluate(seconds, achievedLevel: 1);
    Assert.Equal(expectedLevel, result.Level);
}

[Fact]
public void Evaluate_never_downgrades_an_achieved_level() =>
    Assert.Equal(7, new DefaultBondProgressionPolicy().Evaluate(0, achievedLevel: 7).Level);
```

- [ ] **Step 2: Run the bond tests and verify they fail for missing types**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~DefaultBondProgressionPolicyTests`

Expected: FAIL with missing policy types.

- [ ] **Step 3: Implement the versioned curve and progress result**

```csharp
public sealed class DefaultBondProgressionPolicy : IBondProgressionPolicy
{
    private static readonly long[] Thresholds =
        [0, 3_600, 10_800, 21_600, 36_000, 54_000, 75_600, 100_800, 129_600, 162_000];

    public string Version => "bond-v1";
    public int MaxLevel => 10;

    public BondProgress Evaluate(long lifetimeFocusSeconds, int achievedLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lifetimeFocusSeconds);
        var calculated = Array.FindLastIndex(Thresholds, value => lifetimeFocusSeconds >= value) + 1;
        var level = Math.Clamp(Math.Max(calculated, achievedLevel), 1, MaxLevel);
        var current = Thresholds[level - 1];
        var next = level == MaxLevel ? current : Thresholds[level];
        return new BondProgress(level, lifetimeFocusSeconds, current, next, level == MaxLevel);
    }
}
```

- [ ] **Step 4: Add stable runtime-event and timeline contracts**

```csharp
public sealed record RuntimeEvent(
    string EventId,
    string SessionId,
    string Type,
    DateTimeOffset OccurredAtUtc,
    int CycleNumber,
    FocusPhase Phase,
    string ServantId,
    int ElapsedSeconds,
    int EffectiveSeconds,
    int Priority,
    int SchemaVersion = 1,
    string? PayloadJson = null);

public static class RuntimeEventType
{
    public const string FocusCompleted = "focus_completed";
    public const string FocusStopped = "focus_stopped";
    public const string CycleCompleted = "cycle_completed";
    public const string BondLevelUp = "bond_level_up";
}
```

Use constants rather than persisted C# enum ordinals. Add all Task 1 event names as constants.

- [ ] **Step 5: Make Task 1 transitions produce complete `RuntimeEvent` values**

Generate event IDs through an injected `Func<string>` supplied to the state-machine call or through the command. Tests must pass deterministic IDs such as `event-focus-1`; do not call `Guid.NewGuid()` inside assertions or persistence.

- [ ] **Step 6: Run all Core tests**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release`

Expected: PASS, including all pre-existing Core tests.

- [ ] **Step 7: Commit Task 2**

```powershell
git add src/FgoPet.Core/Events src/FgoPet.Core/Timeline src/FgoPet.Core/Bond src/FgoPet.Core/Focus tests/FgoPet.Core.Tests
git commit -m "feat: add event and bond contracts"
```

---

### Task 3: Versioned SQLite Runtime Database

**Files:**
- Modify: `src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj`
- Create: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabase.cs`
- Create: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Persistence/RuntimeDatabaseTests.cs`

**Interfaces:**
- Consumes: Task 2 stable string/time/seconds conventions.
- Produces: `RuntimeDatabase.Open()` and `RuntimeDatabaseMigrator.Migrate()` for Tasks 4–9.

- [ ] **Step 1: Add the SQLite package and write failing migration tests**

Add to `FgoPet.Infrastructure.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.1" />
</ItemGroup>
```

Test a temporary `runtime.db`:

```csharp
[Fact]
public void Migrate_creates_schema_version_one_and_is_repeatable()
{
    using var database = new RuntimeDatabase(_path);
    new RuntimeDatabaseMigrator(database).Migrate();
    new RuntimeDatabaseMigrator(database).Migrate();

    using var connection = database.Open();
    Assert.Equal(1L, Scalar<long>(connection,
        "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1"));
    Assert.Equal(1L, Scalar<long>(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='focus_sessions'"));
}
```

- [ ] **Step 2: Run the migration test and verify it fails**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~RuntimeDatabaseTests`

Expected: FAIL because runtime database types do not exist.

- [ ] **Step 3: Implement connection setup**

`RuntimeDatabase.Open()` creates the parent directory, opens with `Mode=ReadWriteCreate;Cache=Shared`, enables `foreign_keys=ON`, uses `journal_mode=WAL`, and sets `busy_timeout=5000`. The class stores an absolute database path and never exposes a mutable global connection.

- [ ] **Step 4: Implement explicit schema version 1**

Migration 1 creates exactly these tables and constraints:

```sql
CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);
CREATE TABLE focus_presets(
  preset_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  focus_seconds INTEGER NOT NULL CHECK(focus_seconds BETWEEN 300 AND 10800),
  break_seconds INTEGER NOT NULL CHECK(break_seconds BETWEEN 60 AND 3600),
  cycles INTEGER NOT NULL CHECK(cycles BETWEEN 1 AND 12),
  updated_at_utc TEXT NOT NULL);
CREATE TABLE focus_sessions(
  session_id TEXT PRIMARY KEY,
  status TEXT NOT NULL,
  focus_seconds INTEGER NOT NULL,
  break_seconds INTEGER NOT NULL,
  total_cycles INTEGER NOT NULL,
  current_cycle INTEGER NOT NULL,
  phase TEXT NOT NULL,
  remaining_seconds INTEGER NOT NULL,
  phase_elapsed_seconds INTEGER NOT NULL,
  servant_id TEXT NOT NULL,
  started_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  is_current INTEGER NOT NULL CHECK(is_current IN (0,1)));
CREATE UNIQUE INDEX ux_focus_sessions_current ON focus_sessions(is_current) WHERE is_current=1;
CREATE TABLE runtime_events(
  event_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL,
  type TEXT NOT NULL,
  occurred_at_utc TEXT NOT NULL,
  cycle_number INTEGER NOT NULL,
  phase TEXT NOT NULL,
  servant_id TEXT NOT NULL,
  elapsed_seconds INTEGER NOT NULL,
  effective_seconds INTEGER NOT NULL,
  priority INTEGER NOT NULL,
  schema_version INTEGER NOT NULL,
  payload_json TEXT NULL);
CREATE TABLE timeline_entries(
  entry_id TEXT PRIMARY KEY,
  source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
  occurred_at_utc TEXT NOT NULL,
  type TEXT NOT NULL,
  servant_id TEXT NOT NULL,
  elapsed_seconds INTEGER NOT NULL,
  effective_seconds INTEGER NOT NULL,
  bond_level INTEGER NULL);
CREATE TABLE servant_bonds(
  servant_id TEXT PRIMARY KEY,
  lifetime_focus_seconds INTEGER NOT NULL,
  achieved_level INTEGER NOT NULL,
  policy_version TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL);
CREATE TABLE bond_ledger(
  ledger_id TEXT PRIMARY KEY,
  source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
  servant_id TEXT NOT NULL,
  effective_seconds INTEGER NOT NULL,
  occurred_at_utc TEXT NOT NULL);
```

Execute the migration in one transaction and insert the migration row last. Never catch an error and recreate/delete the database.

- [ ] **Step 5: Add migration rollback and unsupported-version tests**

Verify that a deliberately failing migration leaves no migration row, and a database with version `99` throws `RuntimeDatabaseVersionException` without changing files.

- [ ] **Step 6: Run Infrastructure database tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~RuntimeDatabaseTests`

Expected: PASS.

- [ ] **Step 7: Commit Task 3**

```powershell
git add src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj src/FgoPet.Infrastructure/Persistence tests/FgoPet.Infrastructure.Tests/Persistence
git commit -m "feat: add versioned runtime database"
```

---

### Task 4: Atomic Focus Completion, Timeline, and Bond Persistence

**Files:**
- Create: `src/FgoPet.Infrastructure/Focus/SqliteFocusRepository.cs`
- Create: `src/FgoPet.Infrastructure/Events/SqliteEventStore.cs`
- Create: `src/FgoPet.Infrastructure/Timeline/SqliteTimelineRepository.cs`
- Create: `src/FgoPet.Infrastructure/Bond/SqliteBondRepository.cs`
- Create: `src/FgoPet.Infrastructure/Focus/SqliteFocusCompletionUnit.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Focus/SqliteFocusCompletionUnitTests.cs`

**Interfaces:**
- Consumes: `RuntimeDatabase`, `RuntimeEvent`, `FocusSession`, `IBondProgressionPolicy`.
- Produces: `SaveSnapshot(FocusSession)`, `LoadCurrent()`, `CompleteFocus(FocusSession next, RuntimeEvent completed)`, `QueryToday(DateOnly, TimeZoneInfo, string servantId)`, and `GetBond(string servantId)`.

- [ ] **Step 1: Write failing atomic-completion and idempotency tests**

```csharp
[Fact]
public void CompleteFocus_commits_event_timeline_ledger_and_bond_once()
{
    _unit.CompleteFocus(BreakSession, CompletedEvent);
    _unit.CompleteFocus(BreakSession, CompletedEvent);

    Assert.Equal(1, Count("runtime_events"));
    Assert.Equal(1, Count("timeline_entries"));
    Assert.Equal(1, Count("bond_ledger"));
    Assert.Equal(1_500, Scalar<long>("SELECT lifetime_focus_seconds FROM servant_bonds WHERE servant_id='servant-mash'"));
}

[Fact]
public void CompleteFocus_rolls_back_every_write_when_bond_update_fails()
{
    CorruptBondConstraintForTest();
    Assert.Throws<SqliteException>(() => _unit.CompleteFocus(BreakSession, CompletedEvent));
    Assert.Equal(0, Count("runtime_events"));
    Assert.Equal(0, Count("timeline_entries"));
    Assert.Equal(0, Count("bond_ledger"));
}
```

- [ ] **Step 2: Run the completion tests and verify missing-type failures**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~SqliteFocusCompletionUnitTests`

Expected: FAIL because the repositories and completion unit do not exist.

- [ ] **Step 3: Implement repository methods with caller-owned connections/transactions**

Internal write methods accept `SqliteConnection` and `SqliteTransaction`; they never open or commit their own connection when participating in completion. Public read methods open short-lived connections. Serialize UTC with the round-trip `O` format and parse with `DateTimeStyles.RoundtripKind`.

- [ ] **Step 4: Implement idempotent completion**

`CompleteFocus` begins one transaction, first executes `INSERT OR IGNORE` into `runtime_events`, and returns the existing bond result without further writes if zero rows were inserted. For a new event it updates the current session, writes the timeline projection, writes the unique bond ledger, increments the matching servant row, evaluates progression, and writes a deterministic `bond_level_up` event/entry when the level rises. Commit once at the end.

- [ ] **Step 5: Add today-boundary tests**

Use `Asia/Shanghai` in the test. Convert the requested local date's `[00:00, next 00:00)` boundaries to UTC before querying; never apply SQLite `localtime`. Verify entries at `15:59:59Z` and `16:00:00Z` fall on different local dates.

- [ ] **Step 6: Run all Infrastructure tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release`

Expected: PASS, including Phase 1 package/settings tests.

- [ ] **Step 7: Commit Task 4**

```powershell
git add src/FgoPet.Infrastructure/Focus src/FgoPet.Infrastructure/Events src/FgoPet.Infrastructure/Timeline src/FgoPet.Infrastructure/Bond tests/FgoPet.Infrastructure.Tests/Focus
git commit -m "feat: persist focus completion atomically"
```

---

### Task 5: Focus Session Application Service and Recovery

**Files:**
- Create: `src/FgoPet.App/Focus/IFocusSessionService.cs`
- Create: `src/FgoPet.App/Focus/FocusSessionService.cs`
- Create: `tests/FgoPet.App.Tests/Focus/FocusSessionServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 state machine, Task 4 repositories/completion unit, `TimeProvider`, and `Func<string>` IDs.
- Produces: observable `Current`, `Start`, `Pause`, `Resume`, `Stop`, `Tick`, `Restore`, and `SnapshotChanged` for the panel.

- [ ] **Step 1: Write failing deterministic tick and recovery tests**

```csharp
[Fact]
public void Tick_uses_elapsed_time_not_callback_count()
{
    _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
    _time.Advance(TimeSpan.FromSeconds(7));
    _service.Tick();
    Assert.Equal(1_493, _service.Current.RemainingSeconds);
}

[Fact]
public void Restore_converts_an_active_snapshot_to_paused_without_offline_advance()
{
    _repository.Stored = FocusingWithRemaining(1_200);
    _time.Advance(TimeSpan.FromHours(8));
    _service.Restore();
    Assert.Equal(FocusStatus.PausedFocus, _service.Current.Status);
    Assert.Equal(1_200, _service.Current.RemainingSeconds);
}
```

- [ ] **Step 2: Run service tests and verify failure**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~FocusSessionServiceTests`

Expected: FAIL because the service contract does not exist.

- [ ] **Step 3: Implement orchestration with a monotonic timestamp**

On `Start`/`Resume`, capture `TimeProvider.GetTimestamp()`. On `Tick`, compute elapsed whole seconds through `GetElapsedTime(lastTimestamp, nowTimestamp)`, retain sub-second remainder by advancing the stored timestamp only by consumed whole seconds, and apply one Core transition. A boundary event calls `SqliteFocusCompletionUnit`; other transitions save a snapshot.

- [ ] **Step 4: Add reliable snapshot cadence and write-failure behavior**

Save immediately on every command and boundary. While running, save after each 30 consumed seconds. If a save throws, transition the in-memory state to the matching paused state, raise `PersistenceFailed`, and do not apply further elapsed time until the user retries or stops.

- [ ] **Step 5: Add custom-preset persistence tests**

Verify the last valid custom preset round-trips through `focus_presets` with ID `custom.last`, invalid values never save, and built-in presets are code constants rather than mutable database rows.

- [ ] **Step 6: Run App focus tests and all Core/Infrastructure tests**

Run:

```powershell
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Focus
dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 7: Commit Task 5**

```powershell
git add src/FgoPet.App/Focus tests/FgoPet.App.Tests/Focus
git commit -m "feat: orchestrate recoverable focus sessions"
```

---

### Task 6: Optional Package Dialogue Contract and Feedback Selection

**Files:**
- Create: `src/FgoPet.Core/Packs/DialogueContract.cs`
- Create: `src/FgoPet.Infrastructure/Packs/DialogueManifestReader.cs`
- Create: `src/FgoPet.App/Feedback/EventFeedbackSelector.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Packs/DialogueManifestReaderTests.cs`
- Create: `tests/FgoPet.App.Tests/Feedback/EventFeedbackSelectorTests.cs`
- Create: `tests/fixtures/packs/dialogue-valid/dialogue/manifest.json`
- Create: `tests/fixtures/packs/dialogue-valid/dialogue/zh-CN.json`
- Create: `tests/fixtures/packs/dialogue-invalid-expression/dialogue/manifest.json`
- Create: `tests/fixtures/packs/dialogue-invalid-expression/dialogue/zh-CN.json`

**Interfaces:**
- Consumes: package root, app locale, supported `ExpressionSemantic` values, and Task 2 events.
- Produces: `DialogueBundle? ReadOptional(string packageRoot)`, `FeedbackResult Select(RuntimeEvent, DialogueBundle?, CultureInfo)`.

- [ ] **Step 1: Write failing strict-reader tests**

```csharp
[Fact]
public void ReadOptional_loads_plain_text_candidates_and_default_locale()
{
    var bundle = DialogueManifestReader.ReadOptional(Fixture("dialogue-valid"));
    Assert.NotNull(bundle);
    Assert.Equal("zh-CN", bundle!.DefaultLocale);
    Assert.Equal("focus_started_01", bundle.Localizations["zh-CN"].Events[RuntimeEventType.FocusStarted][0].Id);
}

[Fact]
public void ReadOptional_returns_null_when_dialogue_directory_is_absent() =>
    Assert.Null(DialogueManifestReader.ReadOptional(Fixture("no-dialogue")));
```

- [ ] **Step 2: Run reader tests and verify failure**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~DialogueManifestReaderTests`

Expected: FAIL because the dialogue contracts do not exist.

- [ ] **Step 3: Implement strict safe models and parsing**

Accept schema version 1, relative localization paths under `dialogue/`, candidate IDs matching `^[a-z0-9][a-z0-9._-]{0,63}$`, text from 1 through 160 Unicode scalar values, integer weights 1–100, and only existing eight core expression semantics. Reject unknown JSON properties with existing strict `PackJson` conventions. Do not interpret Markdown, URLs, paths, templates, or condition expressions.

- [ ] **Step 4: Write failing locale/fallback/recent-dedupe selector tests**

Verify exact app locale, package default locale, then neutral fallback. With two candidates and deterministic random order, two consecutive matching events must choose different IDs. Invalid/missing expression returns `ExpressionSemantic.Default`.

- [ ] **Step 5: Implement neutral fallback and bounded recent history**

Keep the last five selected IDs per `(packageId, eventType)` in memory. Neutral application strings are status-only and include no servant name or form of address. Map at least start, pause, resume, focus complete, stop, break start, cycle complete, and bond level-up.

- [ ] **Step 6: Run dialogue and feedback tests**

Run:

```powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~Dialogue
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~EventFeedbackSelectorTests
```

Expected: PASS.

- [ ] **Step 7: Commit Task 6**

```powershell
git add src/FgoPet.Core/Packs/DialogueContract.cs src/FgoPet.Infrastructure/Packs/DialogueManifestReader.cs src/FgoPet.App/Feedback tests/FgoPet.Infrastructure.Tests/Packs/DialogueManifestReaderTests.cs tests/FgoPet.App.Tests/Feedback tests/fixtures/packs/dialogue-valid tests/fixtures/packs/dialogue-invalid-expression
git commit -m "feat: load package focus dialogue"
```

---

### Task 7: Extend the Existing Panel State and ViewModel

**Files:**
- Modify: `src/FgoPet.Core/Panels/AttachedPanelState.cs`
- Modify: `src/FgoPet.Core/Panels/AttachedPanelStateMachine.cs`
- Modify: `tests/FgoPet.Core.Tests/Panels/AttachedPanelStateMachineTests.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs`
- Create: `src/FgoPet.App/Panels/FocusPresetViewModel.cs`
- Create: `src/FgoPet.App/Panels/TimelineItemViewModel.cs`
- Modify: `tests/FgoPet.App.Tests/Panels/AttachedPanelViewModelTests.cs`

**Interfaces:**
- Consumes: `IFocusSessionService`, today query, current servant selection, and Task 6 feedback result.
- Produces: four header commands, compact timer properties, custom validation properties, timeline/bond properties, and the existing `State` property.

- [ ] **Step 1: Write failing state-transition tests for all four columns**

```csharp
[Theory]
[InlineData(AttachedPanelState.Compact, PanelAction.FocusClick, AttachedPanelState.ExpandedFocus)]
[InlineData(AttachedPanelState.ExpandedFocus, PanelAction.FocusClick, AttachedPanelState.Compact)]
[InlineData(AttachedPanelState.ExpandedFocus, PanelAction.TodayClick, AttachedPanelState.ExpandedToday)]
[InlineData(AttachedPanelState.ExpandedToday, PanelAction.TodoClick, AttachedPanelState.ExpandedTodo)]
[InlineData(AttachedPanelState.ExpandedTodo, PanelAction.DialogueClick, AttachedPanelState.ExpandedDialogue)]
public void Four_column_transitions_stretch_or_switch(...)
```

Add tests that `PortraitClick` from every expanded state returns `Collapsed`, while `Escape` returns `Compact`. `ApplyIdle` returns `Compact` for every expanded state but does not do so when `IsEditingCustomPreset` is true; add that boolean parameter explicitly.

- [ ] **Step 2: Run the panel-state tests and verify they fail**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~AttachedPanelStateMachineTests`

Expected: FAIL because focus/today actions and edit suppression are missing.

- [ ] **Step 3: Implement the additive states and exhaustive transitions**

Remove `PanelAction.Collapse` only after all call sites are migrated. Keep current dialogue/Todo behavior. Use a helper `IsExpanded()` to avoid duplicating the four expanded-state checks in idle logic.

- [ ] **Step 4: Write failing ViewModel tests for compact modes and custom validation**

```csharp
[Fact]
public void Running_focus_replaces_compact_message_with_timer_without_expanding()
{
    _focus.Current = FocusingWithRemaining(1_458);
    _focus.RaiseChanged();
    Assert.Equal(AttachedPanelState.Compact, _vm.State);
    Assert.True(_vm.IsCompactTimerVisible);
    Assert.Equal("24:18", _vm.RemainingText);
}

[Fact]
public void Invalid_custom_minutes_disable_start_and_suppress_idle_collapse()
{
    _vm.FocusClick();
    _vm.SelectCustomPreset();
    _vm.CustomFocusMinutes = 4;
    Assert.False(_vm.CanStartFocus);
    Assert.True(_vm.IsEditingCustomPreset);
    Assert.NotEmpty(_vm.CustomFocusError);
}
```

- [ ] **Step 5: Refactor the ViewModel to consume services, not repositories**

Keep panel geometry and persistence out of the ViewModel. Expose `ObservableCollection<TimelineItemViewModel> Today`, `BondLevelText`, `BondRemainingText`, `TodayEffectiveText`, preset selection, custom integer fields, errors, and command methods. Format time in App only. Remove Phase 1 dialogue/Todo fixtures from production startup; keep `Dialogue` collection for Phase 3 compatibility and make Todo empty.

- [ ] **Step 6: Run Core and App panel tests**

Run:

```powershell
dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter FullyQualifiedName~Panels
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Panels
```

Expected: PASS.

- [ ] **Step 7: Commit Task 7**

```powershell
git add src/FgoPet.Core/Panels src/FgoPet.App/Panels tests/FgoPet.Core.Tests/Panels tests/FgoPet.App.Tests/Panels
git commit -m "feat: extend attached panel for phase 2"
```

---

### Task 8: Render Focus, Today, Empty Todo, and Dialogue in the Existing Stretch Panel

**Files:**
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml.cs`
- Modify: `src/FgoPet.App/Main/PortraitWindow.xaml.cs`
- Modify: `tests/FgoPet.Windows.Tests/Panels/AttachedPanelViewIntegrationTests.cs`
- Modify: `tests/FgoPet.Windows.Tests/Windowing/PortraitWindowIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 7 ViewModel properties and state.
- Produces: accessible WPF controls named `FocusContent`, `TodayContent`, `TodoContent`, `DialogueContent`, `CompactMessage`, and `CompactTimer`.

- [ ] **Step 1: Write failing Windows integration tests for visual-state visibility**

```csharp
[Fact]
public void Header_has_four_phase2_columns_and_no_collapse_button()
{
    var view = Sta.Create<AttachedPanelView>();
    Assert.NotNull(view.FindName("FocusButton"));
    Assert.NotNull(view.FindName("TodayButton"));
    Assert.NotNull(view.FindName("TodoButton"));
    Assert.NotNull(view.FindName("DialogueButton"));
    Assert.Null(view.FindName("CollapseButton"));
}

[Fact]
public void Focus_click_stretches_the_same_panel_and_preserves_width_and_anchor()
{
    // Present portrait, capture portrait bounds/panel width/anchor, click Focus,
    // arrange again, then assert portrait bounds, panel width, and panel left/top anchor are unchanged.
}
```

- [ ] **Step 2: Run Windows panel tests and verify failure**

Run: `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --filter FullyQualifiedName~Panels`

Expected: FAIL because focus/today controls are absent and CollapseButton still exists.

- [ ] **Step 3: Modify only the contents inside the approved visual shell**

Preserve the outer `Border` colors, `ApplyPhase0Clip`, margins, font family, width calculation, overlay order, and accent line. Replace header actions with four short text buttons. Add one compact body Grid containing mutually exclusive message/timer views. Add four mutually exclusive `ScrollViewer`/content Grids in row 1 for stretched states.

The custom controls are `−`, editable numeric `TextBox`, and `＋`, with inline magenta validation text. The primary start action uses the existing transparent `LinkButton` family with cyan emphasis, not a filled card/button.

- [ ] **Step 4: Implement state-only visibility switching in code-behind**

`ApplyState()` may set visibility and focus only. Button handlers call ViewModel commands. It must not query SQLite, advance time, compute levels, validate fields, or select dialogue.

- [ ] **Step 5: Preserve stretch geometry for the two new states**

Change `PortraitWindow.ArrangeStablePanelLayout` to treat only `Compact` and `Collapsed` as compact-height cases. All four expanded states use the existing `Math.Min(280, workArea.Height * 0.6 / dpi.Y)` path. Preserve reserved expanded bounds so opening any column cannot move the portrait.

- [ ] **Step 6: Run Windows and App panel tests**

Run:

```powershell
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --filter FullyQualifiedName~Panel
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Panel
```

Expected: PASS.

- [ ] **Step 7: Commit Task 8**

```powershell
git add src/FgoPet.App/Panels src/FgoPet.App/Main/PortraitWindow.xaml.cs tests/FgoPet.Windows.Tests/Panels tests/FgoPet.Windows.Tests/Windowing/PortraitWindowIntegrationTests.cs
git commit -m "feat: render phase 2 attached content"
```

---

### Task 9: Bootstrap, Current-Servant Integration, and Failure Degradation

**Files:**
- Modify: `src/FgoPet.App/Bootstrap/AppPaths.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs`
- Modify: `src/FgoPet.App/Bootstrap/IAppShell.cs` if startup needs an async initialization result
- Modify: `src/FgoPet.App/Main/PortraitWindow.xaml.cs`
- Modify: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs`
- Create: `tests/FgoPet.App.Tests/Bootstrap/Phase2StartupTests.cs`
- Modify: `tests/FgoPet.App.Tests/Bootstrap/PacklessStartupTests.cs`
- Modify: `tests/FgoPet.App.Tests/Bootstrap/DesktopAppShellTests.cs`

**Interfaces:**
- Consumes: all prior tasks plus existing `IArtPackageRepository`, `IPortraitController`, selection settings, and App paths.
- Produces: startup migration/recovery and event feedback applied to the current portrait/panel.

- [ ] **Step 1: Write failing startup and degradation tests**

```csharp
[Fact]
public async Task Startup_migrates_runtime_database_before_restoring_focus()
{
    await _shell.StartAsync([], CancellationToken.None);
    Assert.Equal(new[] { "tray", "migrate", "restore", "portrait" }, _calls);
}

[Fact]
public async Task Migration_failure_keeps_phase1_portrait_available_and_disables_phase2()
{
    _migrator.Exception = new RuntimeDatabaseVersionException(99);
    await _shell.StartAsync([], CancellationToken.None);
    Assert.True(_ui.PortraitShown);
    Assert.False(_phase2Availability.IsAvailable);
}
```

- [ ] **Step 2: Run startup tests and verify failure**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Phase2StartupTests`

Expected: FAIL because migration/recovery are not composed.

- [ ] **Step 3: Add stable runtime paths and DI registrations**

Add `RuntimeDatabasePath = Path.Combine(StorageRoot, "runtime.db")`. Register one `RuntimeDatabase`, migrator, repositories, completion unit, progression policy, focus service, feedback selector, and availability state. Do not replace existing JSON settings or placement stores.

- [ ] **Step 4: Initialize Phase 2 without breaking packless startup**

Initialize the tray first as Phase 1 does. Attempt migration and focus restoration before showing the portrait. If migration fails, log only the exception type/safe message, mark Phase 2 unavailable, and continue the existing pack resolution/show-library/show-portrait flow. No absolute database path appears in user-facing errors.

- [ ] **Step 5: Wire servant identity and feedback**

When a focus stage starts, obtain the stable servant ID from the active resolved pack, not the appearance ID. On emitted feedback, resolve the active package's optional dialogue bundle, select text/expression, add the status to the compact feedback surface, and call the existing portrait controller expression API. A failure in dialogue loading falls back neutrally and never interrupts the timer transaction.

- [ ] **Step 6: Run all App startup tests**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Bootstrap`

Expected: PASS, including packless startup and `.fgopetpack` offering behavior.

- [ ] **Step 7: Commit Task 9**

```powershell
git add src/FgoPet.App/Bootstrap src/FgoPet.App/Main/PortraitWindow.xaml.cs src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs tests/FgoPet.App.Tests/Bootstrap
git commit -m "feat: wire phase 2 runtime startup"
```

---

### Task 10: Full Regression Gate and Phase 2 Manual Matrix

**Files:**
- Verify unchanged: `scripts/test-phase1.ps1`
- Create: `scripts/test-phase2.ps1`
- Create: `docs/testing/phase2-windows-matrix.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: the complete Phase 2 implementation.
- Produces: a repeatable release-mode gate and explicit manual evidence checklist.

- [ ] **Step 1: Extend the existing gate without weakening Phase 1**

Keep `scripts/test-phase1.ps1` byte-for-byte unchanged; its assembly runs automatically discover the added tests. Create `test-phase2.ps1` that first invokes the Phase 1 script, then runs any Phase 2-specific database fixture checks not already part of the four test assemblies. It exits nonzero on any failure and does not stop a running user preview automatically.

- [ ] **Step 2: Run the full Release gate**

Run: `pwsh -NoProfile -File scripts/test-phase2.ps1`

Expected: Release build has 0 warnings and 0 errors; Core, Infrastructure, App, and Windows test assemblies all pass; Phase 1's original 222 tests remain passing within the totals.

- [ ] **Step 3: Create the manual Windows matrix**

The matrix has explicit rows for 150%, 200%, and mixed DPI, and columns for:

- idle Compact character text;
- focusing/paused/break Compact timer;
- Focus/Today/Todo/Dialogue stretch and step-down;
- portrait position before/after every stretch;
- portrait and panel drag/hit behavior;
- 60% height cap and work-area flipping;
- built-in/custom preset validation;
- normal exit and forced-process recovery to paused;
- 25- and 50-minute completion transaction;
- servant switch during a stage and independent bond totals;
- valid, absent, and invalid package dialogue fallback.

Each cell records `pass`, `fail`, or `not-observed` plus evidence path; never infer mixed-monitor results.

- [ ] **Step 4: Document developer and user-visible behavior**

In `README.md`, add only stable Phase 2 behavior: presets, paused recovery, four columns, per-servant bond levels, optional package dialogue, database location category (not a user-specific absolute path), and the test command. State that Todo, LLM, and Agent integrations remain unavailable.

- [ ] **Step 5: Re-run the full gate after documentation/script changes**

Run: `pwsh -NoProfile -File scripts/test-phase2.ps1`

Expected: same all-pass result, 0 warnings, 0 errors.

- [ ] **Step 6: Inspect the final diff for scope and secrets**

Run:

```powershell
git diff --check
rg -n -i "api[_ -]?key|password|C:\\Users\\|D:\\fgo_unpack" src tests docs README.md
git status --short
```

Expected: `git diff --check` has no output; the secret/path scan finds no introduced credential or user-specific runtime path; status contains only intended Phase 2 files.

- [ ] **Step 7: Commit Task 10**

```powershell
git add scripts/test-phase2.ps1 docs/testing/phase2-windows-matrix.md README.md
git commit -m "test: add phase 2 release gate"
```

- [ ] **Step 8: Stop for user manual evidence**

Do not claim Phase 2 formally accepted until the user completes the available Windows matrix rows. Report automated totals separately from manual results and keep release status `deferred`.
