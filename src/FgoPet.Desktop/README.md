# host

## Responsibilities

- `src/FgoPet.App/Composition` is the application composition root. It registers the shell and the capability-owned registration components.
- `FgoPet.DesktopShell` provides generic Windows startup/lifetime, windows, navigation, and presentation composition. It does not own feature business state. `AttachedPanelStateMachine` belongs to Platform.Contracts; Focus owns its compact-view content. The window owns DialogueWindowViewModel, including navigation, focus/read receipts and playback presentation; dialogue messages and editing remain in the shared ConversationViewModel.
- `FgoPet.DataManagement` owns named backup, restore, export, and cleanup workflows. `FgoPet.SettingsHost` owns settings document coordination and schema encoding.
- Memory context setup belongs to the Memory plugin's `MemoryRegistration`, invoked from App composition; the former host-side connector path is gone.

## Contract locations

`SettingsSection` is in `FgoPet.Platform.Contracts/Settings`. `ISettingsNavigator` and `PackageDetailRoute` are in `FgoPet.UiSdk/Settings`. The shell consumes these contracts when composing capability pages and routes. UiSdk also publishes the existing privacy UI request ports; DataManagement supplies their singleton implementations and retains export/deletion policy and confirmation workflows.

## Boundaries and remaining dependencies

The host composes modules and owns cross-cutting data-management workflows, not duplicate module state or feature rules. DesktopShell still has direct project references to Character and Dialogue; those implementation references remain to be consolidated, so the full module boundary migration is not complete. DataManagement still references the Dialogue and Memory implementations for its existing privacy services and page; Memory no longer references a host assembly.

## Safety

Backup, restore, export, and cleanup must preserve maintenance/quiescence gates and safe rollback behavior. Persist only sanitized diagnostics and exceptions.

## Validation

Use `tests/FgoPet.Windows.Tests` for shell and lifecycle integration, `tests/FgoPet.SettingsHost.Tests` for settings coordination, and `tests/FgoPet.EndToEnd.Tests` for backup/restore and other full application flows.
