# host

## Responsibilities

- `src/FgoPet.App/Composition` is the application composition root. It registers the shell and the capability-owned registration components.
- `FgoPet.DesktopShell` provides generic Windows startup/lifetime, windows, navigation, and presentation composition. It does not own feature business state. `AttachedPanelStateMachine` belongs to Platform.Contracts; Focus owns its compact-view content. The window owns DialogueWindowViewModel, including navigation, focus/read receipts and playback presentation; dialogue messages and editing remain in the shared ConversationViewModel.
- The shell places and closes a separate transient window for capability-owned content such as Todo Peek. Settings navigation consumes owner-supplied metadata. Settings pages share one Web root, retained while the window is hidden.
- `FgoPet.DataManagement` owns named backup, restore, export, and cleanup workflows. `FgoPet.SettingsHost` owns settings document coordination and schema encoding.
- Memory context setup belongs to the Memory plugin's `MemoryRegistration`, invoked from App composition; the former host-side connector path is gone.

## Contract locations

`SettingsSection` is in `FgoPet.Platform.Contracts/Settings`. `ISettingsNavigator` and `PackageDetailRoute` are in `FgoPet.UiSdk/Settings`. The shell consumes these contracts when composing capability pages and routes. UiSdk also publishes the existing privacy UI request ports; DataManagement supplies their singleton implementations and retains export/deletion policy and confirmation workflows.

`SettingsWebRootFactory` composes a single Web surface from catalog metadata and owner-supplied `ISettingsWebPage` modules. It restricts commands to the active page, isolates mutable handlers per host, and cancels outstanding page work on navigation or closure. It is the only SettingsWindow entry.

## Boundaries and remaining dependencies

The host composes modules and owns cross-cutting data-management workflows, not duplicate module state or feature rules. DesktopShell still has direct project references to Character and Dialogue; those implementation references remain to be consolidated, so the full module boundary migration is not complete. DataManagement still references the Dialogue and Memory implementations for its existing privacy services and page; Memory no longer references a host assembly.

## Safety

Backup, restore, export, and cleanup must preserve maintenance/quiescence gates and safe rollback behavior. Persist only sanitized diagnostics and exceptions.

## Validation

Use `tests/FgoPet.Windows.Tests` for shell and lifecycle integration, `tests/FgoPet.SettingsHost.Tests` for settings coordination, and `tests/FgoPet.EndToEnd.Tests` for backup/restore and other full application flows.

## Chat window

`DialogueWindow` hosts one Dialogue-owned Chat Web surface, consumes its published session factory, and retains the original shared conversation/window view models. `ChatWebSurfaceFactory` owns the isolated WebView profile, trusted origin, finite command policy and theme subscription. `ChatWebHostActions` maps permitted metadata and actions to existing native owners; workspace navigation opens a separate catalog-owned window and carries the actual workspace/item identity.

Closing normally hides. Focus loss does not hide, hiding stops speech but does not stop generation, and real disposal detaches presentation subscriptions without disposing the shared conversation. Reopening from hidden places the window near the current portrait and clamps its actual dimensions to the work area; invoking an already-visible window only activates it. Size/expansion stays in the current process, and dialogue position is no longer persisted.

## Settings Web presentation

The production SettingsWindow contains one SettingsWebRootFactory surface. Page modules route through their registered ISettingsWebPage owners; the original SettingsViewModel and Web navigation stay synchronized. Native open requests select the requested page even when the window is already visible. Normal close hides and retains the root and its drafts; real disposal detaches navigation/theme subscriptions and closes the surface. The old WPF settings shell, page XAML and legacy constructors have been removed. Agent settings stay unavailable while that feature is not exposed.
