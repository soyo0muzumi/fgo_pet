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

`NativeTodoTools` adapts the same Todo owner to native list/proposal/command ports. A frozen draft, business confirmation, item version and Run idempotency key must all match before one transactional mutation. `todo_agent_receipts` binds the result to conversation/role/project and the exact mutation fingerprint. Unconfirmed outcomes reconcile by reading this receipt; they never replay the write. Receipts are bounded to 4096 rows and cascade with conversation deletion; capacity rejects new writes without evicting receipts. Native drafts cannot be confirmed through the legacy text-confirmation route. Production native policy keeps `todo.create`, `todo.update` and `todo.complete` denied. The bounded P5 approvals for reminder creation/cancellation and focus resume do not enable Todo mutations; `todo.list` and `todo.get` remain read-only.

`todo.get` is a ReadOnly adapter over `TodoApplicationService.Get`. It accepts only one `id` string of 1–128 characters in an arguments object of at most 4096 UTF-8 bytes; blank IDs, duplicate keys and extra properties are rejected. Planned, active and completed items return their current title, description, priority, status, timestamps, checklist IDs/titles/order/completion and `TodoItemVersion` value, matching the version used by `todo.list` and native mutations. Reads do not create drafts, write receipts or change Todo state. Missing items return `TODO_NOT_FOUND`; unavailable reads return `TODO_UNAVAILABLE` without exception details, and cancellation propagates. Successful details are complete: a payload above the host's 64 KiB UTF-8 result limit returns `TOOL_OUTPUT_TOO_LARGE` with no partial content. The existing native scope/fence and read policy remain authoritative; this tool does not enable Command approval or resolve the separate full-catalog budget limit.

Use `plugins/FgoPet.Plugin.Todo/Tests` for Todo, proposal, adapter, and archive behavior; `tests/FgoPet.Infrastructure.Tests` for SQLite persistence; and `tests/FgoPet.Windows.Tests/Todo` and `tests/FgoPet.Windows.Tests/Shell` for workspace and Web surface integration.
