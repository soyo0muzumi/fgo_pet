# work

## Ownership

Todo behavior and state changes are implemented by `FgoPet.Plugin.Todo.Core`: Todo editing and persistence, proposal workflows and confirmation, and the existing work archive use cases. `FgoPet.Plugin.Todo.Desktop` owns the Todo Peek and workspace UI. Peek runs in a short-lived WebView2 transient window. The Web workspace implementation remains a validation candidate while the registered workspace stays WPF until parity and device acceptance.

Agent execution and backend integrations belong to the separate Agent Backend projects. Todo does not dispatch Agent work or consume backend execution state. Backend protocols, connection handling, and execution are not Todo responsibilities.

## Boundaries

The Todo plugin references Extensibility and Platform Storage. Keep business state and archive behavior in the Todo plugin, and keep agent transport/runtime ownership in Agent Backend. Do not add a second Work manager or duplicate Todo/archive state.

The Web adapter uses the existing `TodoApplicationService.Changed` event and a process-local revision. The service and repository remain authoritative; Web snapshots are refreshed after changes and writes use the existing conflict checks. Direct Quick Add is separate from AI proposals, which still require explicit confirmation.

## Safety

Backend authorization remains with Agent Backend. Persist only sanitized archive errors. Proposal confirmation and archive writes must preserve their established idempotency and user-confirmation rules.

## Validation

Use `plugins/FgoPet.Plugin.Todo/Tests` for Todo, proposal, adapter, and archive behavior; `tests/FgoPet.Infrastructure.Tests` for SQLite persistence; and `tests/FgoPet.Windows.Tests/Todo` and `tests/FgoPet.Windows.Tests/Shell` for workspace and Web surface integration.
