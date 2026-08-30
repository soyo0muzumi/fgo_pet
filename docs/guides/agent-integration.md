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

The expanded Todo view has Todo / History / Only today tabs, date groups, a maximum
of eight visible rows, and a thin execution timeline. Work archives are similarly
explicit: completed Todos can be selected for an archive draft, and the user must
confirm before the archive or any long-memory summary is written.

## Privacy and recovery

The Agent connection settings page controls the global connection switch, paired
source allowlists, export of safe Todo/archive rows, and clearing all Agent Todo
data. Clearing removes Todos, executions, event receipts, and work archives while
leaving connection pairing metadata intact so a later re-pair decision is explicit.

On reconnect, persisted non-terminal executions are queried through the gateway and
applied using the same sequence and terminal-state guards as live events. Replayed
or stale events never regress a terminal projection.

## Adapter contract

An adapter should implement the protocol and relay boundary rather than passing
through raw agent output. It may report started, resumed, attention, failed,
cancelled, completed, or goal-completed facts. Completion tools exposed by the Codex
adapter require `user_confirmed=true`; goal completion also requires non-empty task
coverage belonging to the same source identity.

See [the Codex adapter guide](codex-adapter.md) for the local plugin layout and
runtime variables.
