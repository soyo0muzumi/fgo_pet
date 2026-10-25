# host

## Responsibilities

- `src/FgoPet.App/Composition` is the application composition root. It registers the shell and the capability-owned registration components.
- `FgoPet.DesktopShell` provides generic Windows startup/lifetime, windows, navigation, and presentation composition. It does not own feature business state. `AttachedPanelStateMachine` belongs to Platform.Contracts; Focus owns its compact-view content. The window owns DialogueWindowViewModel, including navigation, focus/read receipts and playback presentation; dialogue messages and editing remain in the shared ConversationViewModel.
- The shell places and closes a separate transient window for capability-owned content such as Todo Peek. Settings navigation consumes owner-supplied metadata. Settings pages share one Web root, retained while the window is hidden.
- `FgoPet.DataManagement` owns named backup, restore, export, and cleanup workflows. `FgoPet.SettingsHost` owns settings document coordination and schema encoding.
- Memory context setup belongs to the Memory plugin's `MemoryRegistration`, invoked from App composition; the former host-side connector path is gone.

## Contract locations

The shell owns the approved Mash v2 application artwork in `Shell/Assets/AppIcon.svg`
and its multi-resolution Windows `AppIcon.ico`. DesktopShell embeds the ICO for
portrait, Chat, Settings and tray use; the App project uses the same file for its
native executable icon. Asset provenance and regeneration are documented beside
the source. No character-pack artwork or runtime files are needed to load this icon.

`SettingsSection` is in `FgoPet.Platform.Contracts/Settings`. `ISettingsNavigator` and `PackageDetailRoute` are in `FgoPet.UiSdk/Settings`. The shell consumes these contracts when composing capability pages and routes. UiSdk also publishes the existing privacy UI request ports; DataManagement supplies their singleton implementations and retains export/deletion policy and confirmation workflows.

`SettingsWebRootFactory` composes a single Web surface from catalog metadata and owner-supplied `ISettingsWebPage` modules. It restricts commands to the active page, isolates mutable handlers per host, and cancels outstanding page work on navigation or closure. It is the only SettingsWindow entry.

Theme and ModelConnection use the React ESM modules built from `tools/integrations/margin-settings/`. Shell packages their shared assets under the Settings root's `margin/` directory and allows only local fonts in its existing CSP. These two modules use the available content width for responsive columns; other module layouts, routing and command ownership are unchanged. Build and publish both copy the shared assets and owner entry points.

## Boundaries and remaining dependencies

The host composes modules and owns cross-cutting data-management workflows, not duplicate module state or feature rules. DesktopShell still has direct project references to Character and Dialogue; those implementation references remain to be consolidated, so the full module boundary migration is not complete. DataManagement still references the Dialogue and Memory implementations for its existing privacy services and page; Memory no longer references a host assembly.

## Safety

Backup, restore, export, and cleanup must preserve maintenance/quiescence gates and safe rollback behavior. Persist only sanitized diagnostics and exceptions.

## Validation

Use `tests/FgoPet.Windows.Tests` for shell and lifecycle integration, `tests/FgoPet.SettingsHost.Tests` for settings coordination, and `tests/FgoPet.EndToEnd.Tests` for backup/restore and other full application flows.

## Chat window

The React Chat surface requests composer, conversation and wide presentation through a closed host action. The native owner retains the same WebView, draft and window state while hiding/reopening, anchors size changes to the lower-right bounds and clamps them to the monitor work area. Chat does not inherit the portrait's topmost setting. Its minimum width is 328 DIP; conversation and composer minimum heights are 388 and 182 DIP, constrained by the available work area. The selected-window reader remains a backend capability; Chat exposes no window-text picker, preview or trigger.

The character click, contextual Chat action, tray Chat action and Ctrl+Alt+Space route to the same window. Closing hides it; reopening restores a minimized window and keeps its Web surface, draft and existing pending state. Character visibility is independent of Chat. The shell uses monitor work areas, a 16 DIP gap and margins, side flipping and physical HWND placement; management surfaces take input priority over an overlapping character.

`DialogueWindow` hosts one Dialogue-owned Chat Web surface, consumes its published session factory, and retains the original shared conversation/window view models. `ChatWebSurfaceFactory` owns the isolated WebView profile, trusted origin, finite command policy and theme subscription. `ChatWebHostActions` maps permitted metadata and actions to existing native owners; workspace navigation opens a separate catalog-owned window and carries the actual workspace/item identity.

The React Margin Chat surface uses a paper sheet, compact composer and serif companion thread. Composer, conversation and wide views retain the same Web surface in a borderless native window with a separate drag strip. History and model choices open in panels; the context panel exposes image and workspace actions using host availability. Image transmission details remain available in a collapsed disclosure. Native typography overrides the browser preview's compact mobile sizes. The shell context menu consumes the shared Margin native templates. Production Margin entry wiring suppresses the legacy attached button rail, including transitions from Focus back to Compact. Presentation consumes real snapshots and named bridge commands; it does not reproduce prototype Todo or pending simulations.

The portrait contextual actions route Today to `todo.peek` and Focus to the existing plugin-owned compact content. Focus setup has no entry toolbar. During a running session, the shell reserves transparent room above a ribbon near the portrait's feet for the plugin's hover controls, clamps that room to the work area and hit-tests only visible content. Reopening these entries does not restart the Focus owner or create a second cadence. Setup content receives the container height limit so its own scroll viewer can expose every field in a small work area. Stopping or completing Focus collapses the attached container instead of leaving the legacy entry rail visible.

Closing normally hides. Focus loss does not hide, hiding stops speech but does not stop generation, and real disposal detaches presentation subscriptions without disposing the shared conversation. Chat is placed near the portrait on first open. Its native compact drag strip is separate from the Web content; subsequent reopening and portrait movement retain the current position, constraining bounds to the window's monitor work area when needed. Minimized windows restore normal bounds before placement. Display, work-area and DPI changes recover off-screen bounds without re-anchoring to the portrait; expansion uses the same constraint. Invoking an already-visible window only activates it. Position and size stay in the current process and are not persisted. Settings keeps its existing portrait-relative placement.

## Settings Web presentation

The shared heading owns Back placement. Page cleanup objects may implement `goBack()` to handle nested navigation and return `true`; otherwise the shell returns to the section overview. Native Chat local workspace selection uses the folder picker and the Dialogue-owned `IChatWorkspaceSelection`; the shell neither accepts paths from Web payloads nor owns authorization state.

Local data and backup presentation uses English localization keys and Margin paper sections for export, private backup/restore and deletion. Restore and delete-all retain explicit inline confirmations; paths remain exclusively owned by the native picker. `MarginMemoryDataWebTests` uses synthetic bridge data to check confirmation/cancel dispatch, error redaction and narrow-window actions. It performs no actual file operations; owner and backup round-trip tests remain separate safety evidence.

The production SettingsWindow contains one SettingsWebRootFactory surface. Page modules route through their registered ISettingsWebPage owners; the original SettingsViewModel and Web navigation stay synchronized. Native open requests select the requested page even when the window is already visible. Normal close hides and retains the root and its drafts; real disposal detaches navigation/theme subscriptions and closes the surface. The old WPF settings shell, page XAML and legacy constructors have been removed. Agent settings stay unavailable while that feature is not exposed.

The Margin root presents four fixed sections: Companion, Services, App, and Privacy & Safety. Catalog registration does not create top-level navigation. Existing page IDs, local module validation, command ownership and native deep links remain intact; unknown extension pages appear under App's More settings. Detail pages return to their section, with opaque owner-provided drafts retained by the existing root lifetime. Companion consumes the personalization owner's actual character, size and presence controls; appearance has its separate App entry, and reset remains available under More preferences. Services links to the real model and voice owners without asserting connectivity before their status has been read. Missing access and permissions pages remain visibly unavailable. In narrow windows, the four section buttons wrap into two columns and the main content scrolls independently.
