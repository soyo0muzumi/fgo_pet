# work

## Ownership

Todo behavior and state changes are implemented by `FgoPet.Plugin.Todo.Core`: Todo editing and persistence, proposal workflows and confirmation, and the existing work archive use cases. `FgoPet.Plugin.Todo.Desktop` owns the Todo Peek and workspace UI. Peek runs in a short-lived WebView2 transient window. The Web workspace implementation remains a validation candidate while the registered workspace stays WPF until parity and device acceptance.

Agent execution and backend integrations belong to the separate Agent Backend projects. Todo does not dispatch Agent work or consume backend execution state. Backend protocols, connection handling, and execution are not Todo responsibilities.

## Boundaries

The Todo plugin references Extensibility and Platform Storage. Keep business state and archive behavior in the Todo plugin, and keep agent transport/runtime ownership in Agent Backend. Do not add a second Work manager or duplicate Todo/archive state.

The Web adapter uses the existing `TodoApplicationService.Changed` event and a process-local revision. The service and repository remain authoritative; Web snapshots are refreshed after changes and writes use the existing conflict checks. Direct Quick Add is separate from AI proposals, which still require explicit confirmation.

The Margin Today peek presents active items, due/overdue items and undated items added on the user's current local day. The adapter projects `inProgress`, `dueToday`, `overdue` and `addedToday` from the same local-day calculation; the page maps these keys to English labels in `todo-i18n.js`. It uses the shared paper palette without changing item storage, ETag checks or incomplete-step confirmation. View all remembered things opens the existing full Todo workspace owner.

The registered WPF notebook uses local Margin paper resources and English presentation keys in `TodoWorkspaceStrings`. Derived labels are formatted in Desktop from the existing typed editor sessions. Core remains authoritative for editing, steps, confirmation and undo; the Web workspace is not registered by this presentation change. The notebook header projects the current date and active count, and row metadata uses existing due/creation/completion timestamps. Collapsed rows expose read-only step previews; expanding opens the original editing controls. Local templates provide circular completion controls and small borderless action icons. Narrow layouts reduce horizontal margins and keep editing actions reachable through the existing scroll host.

## Safety

Backend authorization remains with Agent Backend. Persist only sanitized archive errors. Proposal confirmation and archive writes must preserve their established idempotency and user-confirmation rules.

## Validation

`NativeTodoTools` adapts the same Todo owner to native list/proposal/command ports. A frozen draft, business confirmation, item version and Run idempotency key must all match before one transactional mutation. `todo_agent_receipts` binds the result to conversation/role/project and the exact mutation fingerprint. Unconfirmed outcomes reconcile by reading this receipt; they never replay the write. Receipts are bounded to 4096 rows and cascade with conversation deletion; capacity rejects new writes without evicting receipts. Native drafts cannot be confirmed through the legacy text-confirmation route. Production native Command policy remains denied until its approval UI is connected.

Use `plugins/FgoPet.Plugin.Todo/Tests` for Todo, proposal, adapter, and archive behavior; `tests/FgoPet.Infrastructure.Tests` for SQLite persistence; and `tests/FgoPet.Windows.Tests/Todo` and `tests/FgoPet.Windows.Tests/Shell` for workspace and Web surface integration.
