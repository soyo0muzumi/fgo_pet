# Agent integration guide

Phase 4 connects the companion to local Agent adapters without making the app a
terminal, prompt, transcript, or credential viewer.

## Boundary model

The flow is deliberately split into three local boundaries:

1. The adapter observes deterministic lifecycle facts and emits a versioned
   `agent_event` payload.
2. The relay authenticates the paired source, validates and sanitizes the payload,
   deduplicates by source/instance/task/sequence, and queues it for the app.
3. The app projects events into Todo and Agent state, reconnects known non-terminal
   executions, and exposes only bounded summaries and attention state.

The protocol is private and versioned. Payloads reject terminal commands, prompts,
reasoning, tool calls, paths, credentials, transcripts, stdout, and stderr. Private
events are redacted before entering the relay queue. An adapter must be paired by
explicit approval; revoking the source immediately invalidates its credential.

## Todo lifecycle

Todo creation is local and starts in `Planned`. A proposal from the dialogue is
displayed as a card and is not persisted until the user confirms it. Dispatch also
requires confirmation and only accepts a planned Todo. A stable request id makes a
retry idempotent. Offline or rejected dispatches do not activate the Todo.

The expanded Todo view has Todo / History / Only today tabs, date groups, a
scrollable list that retains all matching rows, and a thin execution timeline.
Work archives are similarly explicit: completed Todos can be selected for an
archive draft, and the user must confirm before the archive or any long-memory
summary is written.

## Privacy and recovery

The Agent connection settings page controls the global connection switch, paired
source allowlists, export of safe Todo/archive rows, and clearing all Agent Todo
data. Clearing removes Todos, executions, event receipts, work archives, and relay
pending queues while leaving connection pairing metadata intact so a later re-pair
decision is explicit.

On reconnect, persisted non-terminal executions are queried through the gateway and
applied using the same sequence and terminal-state guards as live events. Replayed
or stale events never regress a terminal projection.

## Running safety and maintenance

If a dispatch transport times out after the remote side may have accepted the
request, the App records `DispatchOutcomeUnknown` and keeps the original
source/instance/task/request identifiers visible as bounded diagnostic data. It
does not automatically retry or replace the execution. The user can explicitly
confirm completed, still running, failed, or cancelled; this writes a local
reconciliation event only and does not call the Relay. A subsequent dispatch is
a new attempt with a new request and execution ID, linked by
`PreviousExecutionId`.

The Agent Connection settings page exposes Relay and Adapter capacity. Archive
candidates are terminal executions older than 30 days whose final event receipt
is present and exact. The App blocks archive while any execution is active or
unknown, while maintenance status is unavailable, or while Relay replay-
protection tombstones are full. After explicit confirmation, the App's durable
archive batch coordinates Relay prepare/commit with the Adapter's local journal.
Unknown network outcomes leave the batch resumable; they never create a
replacement batch or delete records blindly. Completed batches delete full
records and retain compact tombstones so stale events remain rejected.

## Adapter contract

An adapter should implement the protocol and relay boundary rather than passing
through raw agent output. It may report started, resumed, attention, failed,
cancelled, completed, or goal-completed facts. Completion tools exposed by the Codex
adapter require `user_confirmed=true`; goal completion also requires non-empty task
coverage belonging to the same source identity.

See [the Codex adapter guide](codex-adapter.md) for the local plugin layout and
runtime variables.

Accepted app dispatches are held in the paired adapter outbox until the adapter
polls `status_check` with `include_dispatches=true`. The adapter must then pass the
opaque target to its own App Server resolver and emit lifecycle events through the
same relay session.

## Interactive approval and resume

For Codex dispatches, the adapter starts a short-lived local App Server session.
If Codex asks for command or file approval, the adapter does not auto-approve or
cancel the request. It emits an `attention_required` event, persists the dispatch
as `awaiting_acceptance`, and opens a visible `codex resume <thread-id>` session.
The thread ID is carried only as the bounded opaque `remote_task_id`; prompts,
tool arguments, terminal output, and file paths never enter the Agent protocol.

The App attention button uses that same remote ID, so reopening a task targets the
existing Codex thread rather than creating a second task. A resumed session keeps
the original FGO Pet task context through `FGO_PET_AGENT_TASK` and lifecycle hooks
continue to update the existing projection.
