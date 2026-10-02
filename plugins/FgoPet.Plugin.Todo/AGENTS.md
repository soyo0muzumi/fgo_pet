# work Agent Rules

## Scope

Applies to this module tree and inherits the repository root `AGENTS.md`.

## Ownership and boundaries

- `FgoPet.Plugin.Todo.Core` owns Todo behavior/state, proposals and confirmation, and the existing work archive use cases. `FgoPet.Plugin.Todo.Desktop` owns the Todo workspace UI.
- Agent Backend projects independently own agent execution and integrations. Todo neither dispatches Agent work nor consumes backend execution state; it must not absorb backend protocols or runtime ownership.
- Keep Todo/archive state and writes in the Todo plugin. Do not add a parallel Work manager or second source of truth.

## Safety invariants

Backend authorization remains with Agent Backend. Do not persist unsanitized exception details in archives. Keep proposal confirmation and archive writes behind explicit user actions and preserve idempotency guarantees.

## Minimum validation

For Todo/proposal/archive behavior, use `plugins/FgoPet.Plugin.Todo/Tests`. For SQLite persistence use `tests/FgoPet.Infrastructure.Tests`; for workspace integration use `tests/FgoPet.Windows.Tests/Todo`.
