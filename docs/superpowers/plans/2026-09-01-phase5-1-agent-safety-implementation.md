# Phase 5.1 Agent Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户能够安全核对派发结果未知的任务，并通过 App、Relay、Adapter 协调归档已确认终态记录，在不丢失去重证据的前提下恢复 512/4096 容量。

**Architecture:** App SQLite 是业务执行与归档摘要的权威来源；Relay 是跨进程归档协调者；Adapter 是远端执行 journal 的权威来源。归档采用持久化的 `prepare -> commit` 协议：各端先验证并保留原记录，App 写入归档摘要后才提交删除，提交后各端保留最小墓碑。断线、超时和重复请求都依赖稳定的 `archive_batch_id` 幂等恢复，任何核对路径都不得重新派发。

**Tech Stack:** .NET 8, C# 12, WPF, CommunityToolkit.Mvvm, SQLite, named-pipe JSON protocol, DPAPI-protected JSON, xUnit

**Spec:** `docs/superpowers/specs/2026-09-01-phase5-productization-design.md` sections 5.1–5.4

## Global Constraints

- 只实现 Phase 5.1；不实现 `.fgopetbackup`、配置安装引导、角色包 builder、GUI 安装器或正式 Release。
- `dispatch_request_id`、`source_type`、`source_instance` 和远端 `task_id` 一旦生成即保持稳定；刷新、重连、重启、核对和归档重试不得创建新的远端任务。
- “新尝试”不属于普通重试。它必须经过新的显式确认，创建新的 execution ID、task ID 和 dispatch request ID，并保存 `previous_execution_id` 关系。
- App 只显示受控状态和标识，不显示凭据、Prompt、对话正文、本机绝对路径或 Relay/Adapter 状态文件路径。
- 归档候选必须是终态，超过保留窗口，App 已持久化终态事件收据，Adapter 最终事件已被 Relay ACK，且 Relay 对该身份没有待投递消息。
- prepare 失败或中断时任何端都不删除原记录。只有 App 归档摘要持久化后才允许 commit。
- commit 后保留结构化墓碑；旧 dispatch 或旧 event 重放必须 ACK/拒绝为已处理，不能再次执行或再次投影。
- 墓碑达到显式上限时安全拒绝进一步归档，不静默轮替，不通过提高既有限制或清空状态文件绕过容量门禁。
- 所有协议消息都经现有 envelope、身份、长度和 opaque ID 校验；单批最多 128 个候选，响应不包含敏感正文。
- 每个任务遵循 RED → GREEN → focused tests → commit；不要修改用户当前未提交的 Phase 4 文档。

---

## Task 1: Freeze user-facing execution and archive contracts

**Files:**

- Modify: `src/FgoPet.Core/Agents/AgentExecution.cs`
- Modify: `src/FgoPet.Core/Agents/IAgentRepository.cs`
- Create: `src/FgoPet.Core/Agents/AgentArchiveContracts.cs`
- Create: `tests/FgoPet.Core.Tests/Agents/AgentExecutionSafetyTests.cs`
- Create: `tests/FgoPet.Core.Tests/Agents/AgentArchiveContractsTests.cs`

- [ ] **Step 1: Write failing execution-state tests**

Cover these exact rules:

```csharp
[Fact]
public void MarkDispatchOutcomeUnknown_preserves_identity_and_is_non_terminal()
{
    var execution = AgentExecutionFixture.Dispatching();

    var unknown = execution.MarkDispatchOutcomeUnknown(execution.UpdatedAt.AddMinutes(1));

    Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, unknown.Status);
    Assert.Equal(execution.DispatchRequestId, unknown.DispatchRequestId);
    Assert.False(unknown.IsTerminal);
    Assert.False(unknown.ShouldReturnTodoToPlanned);
}

[Fact]
public void CreateNewAttempt_requires_terminal_or_explicitly_abandoned_previous_execution()
{
    var unknown = AgentExecutionFixture.DispatchOutcomeUnknown();

    Assert.Throws<InvalidOperationException>(() =>
        AgentExecution.CreateAttemptAfter(unknown, "new-execution", "new-task", "new-request", unknown.UpdatedAt));
}
```

Add `DispatchOutcomeUnknown` to `AgentExecutionStatus`, add `MarkDispatchOutcomeUnknown`, and add nullable `PreviousExecutionId`. Do not overload `Attention` or `Failed`: both carry different user semantics.

- [ ] **Step 2: Run the focused RED test**

Run:

```powershell
dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentExecutionSafetyTests
```

Expected: compile failure because the new state and methods do not exist.

- [ ] **Step 3: Add immutable archive contracts and validation tests**

Use one normalized identity throughout the three processes:

```csharp
public sealed record AgentArchiveIdentity(
    string SourceType,
    string SourceInstance,
    string TaskId,
    string DispatchRequestId,
    long FinalSequence,
    AgentExecutionStatus FinalStatus);

public sealed record AgentArchiveCandidate(
    string ExecutionId,
    AgentArchiveIdentity Identity,
    DateTimeOffset EndedAt,
    string SummarySha256);

public sealed record AgentArchiveBatch(
    string BatchId,
    DateTimeOffset CreatedAt,
    AgentArchiveBatchState State,
    IReadOnlyList<AgentArchiveCandidate> Candidates,
    string BatchSha256,
    string? SafeError = null);
```

Define states `Preparing`, `Prepared`, `CommitPending`, `Completed`, `Rejected`. Constructors must reject non-terminal status, missing `EndedAt`, negative sequence, duplicate identities, non-uppercase 64-character SHA-256, empty batch, or more than 128 candidates.

- [ ] **Step 4: Extend the repository boundary without IO details**

Add:

```csharp
IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit);
bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence);
void SaveArchiveBatch(AgentArchiveBatch batch);
AgentArchiveBatch? GetArchiveBatch(string batchId);
IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches();
```

The repository contract does not delete executions yet. Deletion is added only after the coordinated commit path exists.

- [ ] **Step 5: Implement the minimum domain code and run focused tests**

Run:

```powershell
dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentExecutionSafetyTests|FullyQualifiedName~AgentArchiveContractsTests"
```

Expected: all new Core tests pass.

- [ ] **Step 6: Commit Task 1**

```powershell
git add src/FgoPet.Core/Agents/AgentExecution.cs src/FgoPet.Core/Agents/IAgentRepository.cs src/FgoPet.Core/Agents/AgentArchiveContracts.cs tests/FgoPet.Core.Tests/Agents/AgentExecutionSafetyTests.cs tests/FgoPet.Core.Tests/Agents/AgentArchiveContractsTests.cs
git commit -m "feat(agent): define reconciliation and archive contracts"
```

---

## Task 2: Persist unknown outcomes and archive batches in SQLite

**Files:**

- Modify: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs`
- Modify: `src/FgoPet.Infrastructure/Persistence/SqliteAgentRepository.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Persistence/RuntimeDatabaseTests.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Persistence/SqliteAgentRepositoryTests.cs`

- [ ] **Step 1: Write failing migration and round-trip tests**

Add a migration after the current latest version with:

```sql
ALTER TABLE agent_executions ADD COLUMN previous_execution_id TEXT NULL;

CREATE TABLE agent_archive_batches(
    batch_id TEXT PRIMARY KEY,
    created_at_utc TEXT NOT NULL,
    state TEXT NOT NULL,
    batch_sha256 TEXT NOT NULL,
    safe_error TEXT NULL
);

CREATE TABLE agent_archive_items(
    batch_id TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    source_type TEXT NOT NULL,
    source_instance TEXT NOT NULL,
    task_id TEXT NOT NULL,
    dispatch_request_id TEXT NOT NULL,
    final_sequence INTEGER NOT NULL,
    final_status TEXT NOT NULL,
    ended_at_utc TEXT NOT NULL,
    summary_sha256 TEXT NOT NULL,
    PRIMARY KEY(batch_id, execution_id),
    UNIQUE(batch_id, source_type, source_instance, task_id, dispatch_request_id),
    FOREIGN KEY(batch_id) REFERENCES agent_archive_batches(batch_id) ON DELETE CASCADE
);
```

Tests must prove upgrade from the prior schema, unknown-status round trip, `PreviousExecutionId` round trip, ordered terminal listing, exact event-receipt lookup, archive-batch transactionality, and incomplete-batch recovery.

- [ ] **Step 2: Run the focused RED test**

```powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RuntimeDatabaseTests|FullyQualifiedName~SqliteAgentRepositoryTests"
```

Expected: new assertions fail because the migration and repository methods are absent.

- [ ] **Step 3: Implement one-transaction archive persistence**

`SaveArchiveBatch` must insert/update the header and replace its items in the same SQLite transaction. `ListTerminalExecutions` orders by `ended_at_utc`, then `id`, and applies `LIMIT`. `HasEventReceipt` uses the existing receipt identity and sequence columns; it must not infer receipt from execution status.

- [ ] **Step 4: Add commit-time pruning transaction**

Extend `IAgentRepository` and the SQLite implementation with:

```csharp
void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt);
```

In a single transaction, verify the batch is `CommitPending`, delete only the covered `agent_event_receipts` and full `agent_executions`, preserve the batch/item summaries, then mark `Completed`. A repeated call for an already completed batch returns successfully without changing rows.

- [ ] **Step 5: Run focused persistence tests**

```powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RuntimeDatabaseTests|FullyQualifiedName~SqliteAgentRepositoryTests"
```

Expected: focused tests pass with no warnings.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/FgoPet.Core/Agents/IAgentRepository.cs src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs src/FgoPet.Infrastructure/Persistence/SqliteAgentRepository.cs tests/FgoPet.Infrastructure.Tests/Persistence/RuntimeDatabaseTests.cs tests/FgoPet.Infrastructure.Tests/Persistence/SqliteAgentRepositoryTests.cs
git commit -m "feat(agent): persist reconciliation and archive batches"
```

---

## Task 3: Add bounded maintenance protocol messages

**Files:**

- Modify: `src/FgoPet.AgentProtocol/Messages/RelayAdministrationMessages.cs`
- Create: `src/FgoPet.AgentProtocol/Messages/AgentMaintenanceMessages.cs`
- Modify: `src/FgoPet.AgentProtocol/Validation/AgentProtocolValidator.cs`
- Modify: `tests/FgoPet.AgentProtocol.Tests/Fixtures/ProtocolFixtureTests.cs`
- Modify: `tests/FgoPet.AgentProtocol.Tests/Fixtures/RelayResponseContractTests.cs`
- Create: `tests/FgoPet.AgentProtocol.Tests/Fixtures/AgentMaintenanceContractTests.cs`

- [ ] **Step 1: Write failing serialization and boundary tests**

Define these operations:

```text
App -> Relay: maintenance_status, archive_prepare, archive_commit
Adapter -> Relay: maintenance_sync
Relay -> Adapter: prepare batch, commit batch, or no-op
Adapter -> Relay: prepared, committed, or rejected acknowledgement
```

The public DTO shape is:

```csharp
public sealed record AgentCapacityCounter(string Name, int Used, int Limit, int Archivable);
public sealed record AgentMaintenanceStatusResponse(
    IReadOnlyList<AgentCapacityCounter> Counters,
    DateTimeOffset? OldestArchivableAt,
    string? ActiveBatchId,
    string? SafeError);

public sealed record AgentArchivePrepareRequest(string BatchId, IReadOnlyList<AgentArchiveProtocolItem> Items, string BatchSha256);
public sealed record AgentArchiveCommitRequest(string BatchId, string BatchSha256);
public sealed record AdapterMaintenanceSyncRequest(
    string SourceType,
    string SourceInstance,
    string? AcknowledgedBatchId,
    string? AcknowledgedPhase,
    string? SafeError,
    AgentCapacityCounter AdapterJournal);
```

Protocol tests cover 0 and 129 items, duplicate identity, mismatched source instance, invalid SHA-256, negative counters, oversized safe error, unknown operation, and JSON fixture round trips.

- [ ] **Step 2: Run the focused RED test**

```powershell
dotnet test tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentMaintenanceContractTests
```

Expected: compile failure because maintenance messages do not exist.

- [ ] **Step 3: Implement DTOs and centralized validation**

Reuse `AgentIdentityValidation`/protocol guard semantics rather than accepting free-form paths or payloads. `SummarySha256` and `BatchSha256` are hashes only; no Todo title, prompt, summary text, or filesystem path crosses this protocol.

- [ ] **Step 4: Run all protocol fixture tests**

```powershell
dotnet test tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj -c Release --no-restore
```

Expected: all protocol tests pass.

- [ ] **Step 5: Commit Task 3**

```powershell
git add src/FgoPet.AgentProtocol/Messages/RelayAdministrationMessages.cs src/FgoPet.AgentProtocol/Messages/AgentMaintenanceMessages.cs src/FgoPet.AgentProtocol/Validation/AgentProtocolValidator.cs tests/FgoPet.AgentProtocol.Tests/Fixtures/ProtocolFixtureTests.cs tests/FgoPet.AgentProtocol.Tests/Fixtures/RelayResponseContractTests.cs tests/FgoPet.AgentProtocol.Tests/Fixtures/AgentMaintenanceContractTests.cs
git commit -m "feat(protocol): add agent maintenance messages"
```

---

## Task 4: Make Relay the durable archive coordinator

**Files:**

- Modify: `src/FgoPet.AgentRelay/Storage/RelayState.cs`
- Modify: `src/FgoPet.AgentRelay/Storage/RelayStore.cs`
- Modify: `src/FgoPet.AgentRelay/Routing/RelayRouter.cs`
- Modify: `src/FgoPet.AgentRelay/Pipes/AppPipeServer.cs`
- Modify: `src/FgoPet.AgentRelay/Pipes/AdapterPipeServer.cs`
- Modify: `tests/FgoPet.AgentRelay.Tests/ProtectedRelayStateStoreTests.cs`
- Modify: `tests/FgoPet.AgentRelay.Tests/RelayRouterTests.cs`
- Modify: `tests/FgoPet.AgentRelay.Tests/RelayPipeIntegrationTests.cs`

- [ ] **Step 1: Write failing Relay state and coordinator tests**

Upgrade `RelayState` to schema version 2 and add:

```csharp
IReadOnlyList<RelayArchiveBatchState> ArchiveBatches
IReadOnlyList<AgentArchiveTombstone> ArchiveTombstones
IReadOnlyList<AdapterCapacityReport> AdapterCapacityReports
```

Tests must prove v1 state migrates to v2 with empty maintenance collections, corrupt v2 state is rejected, and DPAPI-protected round trip preserves prepare/commit state.

- [ ] **Step 2: Specify prepare rejection before implementation**

For every requested identity Relay must confirm:

- no matching item in `_outbound`;
- no matching item in `_inbound`;
- dispatch receipt exists, matches source instance, and is acknowledged;
- inbound watermark exists with `Sequence >= FinalSequence`;
- source grant still identifies the same adapter instance;
- no conflicting batch owns the identity;
- tombstone does not conflict with the supplied batch hash.

Write tests for each rejection and assert the original counts are unchanged.

- [ ] **Step 3: Implement durable prepare and adapter synchronization**

`archive_prepare` persists `AwaitingAdapterPrepare` before responding. `maintenance_sync` returns the next command for only the authenticated adapter identity. Adapter `prepared` ACK moves the batch to `Prepared`; rejection persists `Rejected` with a bounded safe error and retains every receipt/watermark.

- [ ] **Step 4: Implement idempotent commit and Relay tombstones**

`archive_commit` is accepted only for a matching `Prepared` batch/hash and persists `AwaitingAdapterCommit`. After Adapter `committed` ACK, Relay atomically:

1. inserts one tombstone per identity;
2. removes covered dispatch receipts and watermarks;
3. marks the batch `Completed`;
4. retains the compact batch summary.

Set `MaxArchiveTombstones = 16384`. Refuse prepare when the batch would exceed it. Old event replay at or below a matching tombstone sequence returns the existing duplicate/acknowledged result without enqueueing.

- [ ] **Step 5: Expose read-only capacity status**

Return counters for `relay_dispatch_receipts`, `relay_event_watermarks`, `relay_inbound_queue`, `relay_outbound_queue`, and the latest authenticated `adapter_journal` report. Approaching-full is `Used >= 80% of Limit`; full remains a hard refusal only for new dispatches, while ACK and terminal delivery paths remain available.

- [ ] **Step 6: Run Relay tests**

```powershell
dotnet test tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ProtectedRelayStateStoreTests|FullyQualifiedName~RelayRouterTests|FullyQualifiedName~RelayPipeIntegrationTests"
```

Expected: focused Relay tests pass, including restart between every prepare/commit transition.

- [ ] **Step 7: Commit Task 4**

```powershell
git add src/FgoPet.AgentRelay/Storage/RelayState.cs src/FgoPet.AgentRelay/Storage/RelayStore.cs src/FgoPet.AgentRelay/Routing/RelayRouter.cs src/FgoPet.AgentRelay/Pipes/AppPipeServer.cs src/FgoPet.AgentRelay/Pipes/AdapterPipeServer.cs tests/FgoPet.AgentRelay.Tests/ProtectedRelayStateStoreTests.cs tests/FgoPet.AgentRelay.Tests/RelayRouterTests.cs tests/FgoPet.AgentRelay.Tests/RelayPipeIntegrationTests.cs
git commit -m "feat(relay): coordinate safe execution archives"
```

---

## Task 5: Prepare and commit Adapter journal pruning

**Files:**

- Modify: `src/FgoPet.CodexAdapter/Relay/ICodexRelayConnector.cs`
- Modify: `src/FgoPet.CodexAdapter/Relay/CodexRelayConnector.cs`
- Modify: `src/FgoPet.CodexAdapter/AppServer/CodexDispatchWorker.cs`
- Create: `src/FgoPet.CodexAdapter/AppServer/CodexArchiveState.cs`
- Modify: `tests/FgoPet.CodexAdapter.Tests/CodexRelayConnectorTests.cs`
- Modify: `tests/FgoPet.CodexAdapter.Tests/CodexDispatchWorkerTests.cs`

- [ ] **Step 1: Write failing worker safety tests**

Tests must reject prepare when a record is missing, non-terminal, has `PendingEvent`, has a mismatched task/request identity, has a lower terminal sequence, or hashes differently. They must assert that `_records` and the protected file are unchanged.

- [ ] **Step 2: Add a separate protected archive-state store**

Use `dispatch-archives-{identityKey}.v1.json`, storing active batch phase and compact tombstones. A tombstone contains source identity, task/request IDs, final sequence/status, batch ID, and hashes; it contains no request prompt or terminal summary text.

- [ ] **Step 3: Implement maintenance sync before polling new dispatches**

In each connected loop:

1. report journal capacity;
2. deliver previous maintenance ACK;
3. receive at most one prepare/commit command;
4. process and persist it;
5. sync again to return the ACK;
6. only then poll new dispatches.

Prepare persists the verified candidate set but retains full records. Commit first writes tombstones and committed batch state, then rewrites the journal without covered records. If the second write fails, startup sees committed tombstones plus surviving records and completes the same prune idempotently before executing work.

- [ ] **Step 4: Enforce tombstone replay protection**

When a dispatch request matches a tombstone, do not call `ICodexTaskExecutor`; return/ACK it as already handled. When a terminal event replay is requested for an archived record, report the tombstone outcome instead of reconstructing event text.

- [ ] **Step 5: Run Adapter tests**

```powershell
dotnet test tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CodexRelayConnectorTests|FullyQualifiedName~CodexDispatchWorkerTests"
```

Expected: focused tests pass, including failures before prepare save, after prepare save, after tombstone save, and after journal prune.

- [ ] **Step 6: Commit Task 5**

```powershell
git add src/FgoPet.CodexAdapter/Relay/ICodexRelayConnector.cs src/FgoPet.CodexAdapter/Relay/CodexRelayConnector.cs src/FgoPet.CodexAdapter/AppServer/CodexDispatchWorker.cs src/FgoPet.CodexAdapter/AppServer/CodexArchiveState.cs tests/FgoPet.CodexAdapter.Tests/CodexRelayConnectorTests.cs tests/FgoPet.CodexAdapter.Tests/CodexDispatchWorkerTests.cs
git commit -m "feat(adapter): archive terminal dispatch journal records"
```

---

## Task 6: Orchestrate archival from the App administration boundary

**Files:**

- Modify: `src/FgoPet.Core/Agents/AgentRelayAdministration.cs`
- Modify: `src/FgoPet.Infrastructure/Agents/AgentControlClient.cs`
- Modify: `src/FgoPet.Infrastructure/Agents/AgentRelayAdministration.cs`
- Create: `src/FgoPet.App/Services/AgentArchiveService.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Agents/AgentRelayAdministrationTests.cs`
- Create: `tests/FgoPet.App.Tests/Services/AgentArchiveServiceTests.cs`

- [ ] **Step 1: Add failing administration and orchestration tests**

Extend `IAgentRelayAdministration` with:

```csharp
Task<AgentMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default);
Task<AgentArchivePrepareResult> PrepareArchiveAsync(AgentArchiveBatch batch, CancellationToken cancellationToken = default);
Task<AgentArchiveCommitResult> CommitArchiveAsync(string batchId, string batchSha256, CancellationToken cancellationToken = default);
```

Tests must prove timeout returns a bounded safe failure and does not retry a mutation automatically.

- [ ] **Step 2: Implement deterministic candidate construction**

`AgentArchiveService.BuildCandidates` selects terminal executions older than a default 30-day retention cutoff, requires an exact final receipt, orders by `(EndedAt, ExecutionId)`, caps at 128, hashes each normalized identity, then hashes the ordered item hashes to form `BatchSha256`. Inject `TimeProvider`; do not use ambient local time.

- [ ] **Step 3: Implement the orchestration state machine**

```text
Build candidates
  -> Save Preparing in SQLite
  -> Relay prepare
  -> Save Prepared in SQLite
  -> Save CommitPending in SQLite
  -> Relay commit
  -> CompleteArchiveBatch SQLite transaction
  -> Save Completed if not already completed
```

If Relay returns `prepared` after the App times out, the next run resumes by batch ID. If Relay commit is known complete but the response is lost, the next run repeats commit with the same batch ID/hash, then completes SQLite pruning. Never create a replacement batch for an incomplete batch.

- [ ] **Step 4: Test every interruption boundary**

Use fakes to stop before and after each durable write and network call. After recreating the service from persisted state, assert convergence to either `Rejected` with all source records intact or `Completed` with summaries/tombstones intact.

- [ ] **Step 5: Run focused App and Infrastructure tests**

```powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentRelayAdministrationTests
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentArchiveServiceTests
```

Expected: all focused orchestration tests pass.

- [ ] **Step 6: Commit Task 6**

```powershell
git add src/FgoPet.Core/Agents/AgentRelayAdministration.cs src/FgoPet.Infrastructure/Agents/AgentControlClient.cs src/FgoPet.Infrastructure/Agents/AgentRelayAdministration.cs src/FgoPet.App/Services/AgentArchiveService.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.Infrastructure.Tests/Agents/AgentRelayAdministrationTests.cs tests/FgoPet.App.Tests/Services/AgentArchiveServiceTests.cs
git commit -m "feat(app): orchestrate agent record archives"
```

---

## Task 7: Present reconciliation and archive controls in existing surfaces

**Files:**

- Modify: `src/FgoPet.App/Services/AgentDispatchService.cs`
- Modify: `src/FgoPet.App/ViewModels/TodoListViewModel.cs`
- Modify: `src/FgoPet.App/ViewModels/TodoItemViewModel.cs`
- Modify: `src/FgoPet.App/ViewModels/AgentCurrentTaskViewModel.cs`
- Modify: `src/FgoPet.App/Views/ExpandedTodoView.xaml`
- Modify: `src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs`
- Modify: `src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml`
- Modify: `src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs`
- Modify: `tests/FgoPet.App.Tests/ViewModels/AgentDispatchDialogViewModelTests.cs`
- Modify: `tests/FgoPet.App.Tests/ViewModels/AgentCurrentTaskViewModelTests.cs`
- Modify: `tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs`
- Modify: `tests/FgoPet.Windows.Tests/Settings/AgentConnectionPageTests.cs`

- [ ] **Step 1: Persist unknown outcome at the dispatch boundary**

Write a failing test showing `dispatch_outcome_unknown` changes the reserved execution from `Dispatching` to `DispatchOutcomeUnknown`, retains every identity field, and does not restore Todo to `Planned`. Implement this before UI work.

- [ ] **Step 2: Project execution state into Todo rows**

Change `TodoListViewModel` to load the latest execution for each Todo and pass it to `TodoItemViewModel`. Required labels/actions:

| Condition | Label | Primary safe action |
|---|---|---|
| `Active` / `Attention` | 执行中 / 需要处理 | 打开 Agent 任务 |
| `DispatchOutcomeUnknown` | 待核对 | 打开 Codex |
| Relay offline | 等待连接 | 打开 Agent 设置 |
| grant revoked | 需要授权 | 打开 Agent 设置 |
| terminal | 已完成 / 已失败 / 已取消 | 查看历史 |

For `待核对`, expose commands to open the persisted task if a remote task ID exists and copy a diagnostic block containing only source display name, source instance ID, task ID, dispatch request ID, and execution timestamp. Do not add a “再次派发” button.

- [ ] **Step 3: Add explicit manual outcome confirmation**

The reconciliation panel may mark the existing attempt completed, failed, or cancelled only after a confirmation dialog that names the execution ID. It updates the existing execution and Todo projection; it never sends a Relay request. A separate “创建新尝试” action is enabled only after the old attempt is terminal and opens the normal dispatch confirmation dialog, producing all-new IDs plus `PreviousExecutionId`.

- [ ] **Step 4: Add read-only capacity and confirmed archive controls**

In the existing Agent settings page add one “运行记录容量” card. Show each named counter as `Used / Limit`, `Archivable`, oldest eligible date, incomplete batch state, and warning text at 80%. The archive button:

- is disabled with no eligible candidates, active work, offline maintenance path, or tombstone exhaustion;
- opens a confirmation dialog with candidate count and irreversible full-record deletion wording;
- calls `AgentArchiveService` once;
- on unknown result shows the batch ID and “刷新状态”，not a second archive command.

- [ ] **Step 5: Add WPF accessibility tests**

Assert the capacity card and reconciliation controls have explicit `AutomationProperties.Name`, keyboard focusability, no raw state-file path, no credential text, and no enabled retry-dispatch control in the unknown state.

- [ ] **Step 6: Run focused UI tests**

```powershell
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentDispatchDialogViewModelTests|FullyQualifiedName~AgentCurrentTaskViewModelTests|FullyQualifiedName~AgentConnectionSettingsViewModelTests"
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentConnectionPageTests
```

Expected: all focused view-model and WPF tests pass.

- [ ] **Step 7: Commit Task 7**

```powershell
git add src/FgoPet.App/Services/AgentDispatchService.cs src/FgoPet.App/ViewModels/TodoListViewModel.cs src/FgoPet.App/ViewModels/TodoItemViewModel.cs src/FgoPet.App/ViewModels/AgentCurrentTaskViewModel.cs src/FgoPet.App/Views/ExpandedTodoView.xaml src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs tests/FgoPet.App.Tests/ViewModels/AgentDispatchDialogViewModelTests.cs tests/FgoPet.App.Tests/ViewModels/AgentCurrentTaskViewModelTests.cs tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs tests/FgoPet.Windows.Tests/Settings/AgentConnectionPageTests.cs
git commit -m "feat(ui): add agent reconciliation and archive controls"
```

---

## Task 8: Verify cross-process safety, capacity recovery, and documentation

**Files:**

- Modify: `tests/FgoPet.EndToEnd.Tests/AgentIntegrationEndToEndTests.cs`
- Modify: `tests/FgoPet.EndToEnd.Tests/AppRelayControlTests.cs`
- Create: `tests/FgoPet.EndToEnd.Tests/AgentArchiveEndToEndTests.cs`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Create: `docs/reports/2026-09-01-phase5-1-agent-safety-acceptance.md`

- [ ] **Step 1: Add end-to-end failure matrix tests**

For a real App repository, Relay state file, named-pipe clients, and Adapter protected stores, cover:

1. dispatch response timeout followed by App/Relay/Adapter restart creates exactly one executor call;
2. prepare while an event is pending is rejected with no deletions;
3. restart after Relay prepare resumes the same batch;
4. restart after Adapter prepared ACK retains full records;
5. lost commit response converges after same-ID retry;
6. old dispatch replay after completion does not call executor;
7. old event replay after completion does not apply a second Todo transition;
8. filling Adapter to 512 and Relay receipts/watermarks near 4096, then archiving eligible terminal records, admits new work without changing constants;
9. tombstone limit refuses the next archive and preserves full source records;
10. archive diagnostic output contains no prompt, Todo description, credential, or absolute path.

- [ ] **Step 2: Run the end-to-end tests**

```powershell
dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentArchiveEndToEndTests|FullyQualifiedName~AgentIntegrationEndToEndTests|FullyQualifiedName~AppRelayControlTests"
```

Expected: all focused end-to-end tests pass.

- [ ] **Step 3: Update both README languages in parallel**

In `README.md` and `README.zh-CN.md`, document the user-visible meanings of running, needs review, waiting for connection, needs authorization, and terminal states; explain that “needs review” never retries automatically; document the capacity card, 30-day default eligibility, explicit confirmation, irreversibility of full-record pruning, and retained tombstones. Keep English as the default README and preserve the top language switch on both pages.

- [ ] **Step 4: Run the full verification gate**

```powershell
dotnet build FgoPet.sln -c Release --no-restore -warnaserror
dotnet test FgoPet.sln -c Release --no-build --no-restore
& 'D:/environments/anaconda/python.exe' -m pytest -q -p no:cacheprovider --basetemp D:/fgo_unpack/.pytest-phase5-1
git diff --check
```

Expected:

- Release build exits 0 with 0 warnings and 0 errors;
- all .NET tests pass;
- all Python tests pass;
- `git diff --check` reports no whitespace errors.

- [ ] **Step 5: Perform manual acceptance**

Record exact evidence in `docs/reports/2026-09-01-phase5-1-agent-safety-acceptance.md`:

- unknown outcome survives App restart and shows the same request/task IDs;
- Open Codex and Copy identifiers work without exposing paths or credentials;
- no action in the reconciliation UI creates a new task without a fresh confirmation;
- capacity warning appears before full and full blocks only new dispatch;
- disconnecting each process during prepare/commit leaves an understandable resumable batch state;
- completed archive restores capacity and old replays remain deduplicated.

- [ ] **Step 6: Commit Task 8**

```powershell
git add tests/FgoPet.EndToEnd.Tests/AgentIntegrationEndToEndTests.cs tests/FgoPet.EndToEnd.Tests/AppRelayControlTests.cs tests/FgoPet.EndToEnd.Tests/AgentArchiveEndToEndTests.cs README.md README.zh-CN.md docs/reports/2026-09-01-phase5-1-agent-safety-acceptance.md
git commit -m "test(agent): verify phase 5.1 safety guarantees"
```

---

## Plan Completion Gate

- [ ] Every spec rule in sections 5.1–5.4 is covered by at least one automated or recorded manual acceptance check.
- [ ] Unknown outcomes retain stable identity and cannot trigger a blind retry.
- [ ] New attempts require fresh confirmation and use new IDs linked by `PreviousExecutionId`.
- [ ] Prepare never deletes records; commit is impossible before App persists the archive summary.
- [ ] Restart at every durable transition converges using the same batch ID and hash.
- [ ] App, Relay, and Adapter retain compatible tombstones after full-record pruning.
- [ ] 512/4096 capacity recovery is demonstrated without increasing constants or deleting state files.
- [ ] Sensitive prompts, descriptions, credentials, and absolute paths never enter protocol status, tombstones, diagnostics, or acceptance logs.
- [ ] Both README languages describe the same behavior, with English remaining the default entry.
- [ ] Full Release build, .NET suite, Python suite, and whitespace checks pass before Phase 5.1 is marked complete.
