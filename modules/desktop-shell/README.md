# desktop-shell

## Responsibilities

Own the Windows desktop shell, application lifetime, windows, tray, attached-panel container, page navigation, portrait host, and composition root. DialogueWindow is the unique chat/settings surface and PortraitWindow remains the desktop pet; the composition root assembles the feature modules without parallel window shells.

## Non-responsibilities

Feature-specific views, view models, controls, styles, domain state, and business workflows belong to their owning modules.

## Current source and tests

Current source is in `src/FgoPet.App/{Bootstrap,Lifetime,Main,Panels,Portraits,Runtime,Tray,Windowing}` where code is shell or cross-feature composition. Current tests are the shell-only subsets of `tests/FgoPet.App.Tests/{Bootstrap,Main,Panels,Portraits,Runtime,Windowing}` and `tests/FgoPet.Windows.Tests/{Lifetime,Panels,Soak,Theming,Windowing}`.

## Target layout

Move shell-owned code and shell-only tests under this module while preserving assembly names and namespaces during the first migration stable point.

## Public interfaces

Application lifetime, shell composition, navigation, window placement, attached-panel hosting, portrait hosting, and startup contracts exposed to feature modules.

## Dependencies

May depend on all business modules and `ui-foundation`; all feature modules may not depend on this module. It ultimately depends on `foundation` through the normal module direction.

## Data and security boundaries

Own shell state, window placement, and local UI lifecycle only. Do not persist feature data here, execute package content, or bypass module authorization and privacy boundaries.

## Development and validation

Run the affected App and Windows tests, then `dotnet build FgoPet.sln -c Release -warnaserror` and the relevant integration tests for startup or window behavior.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

Shell responsibilities are currently interleaved with feature presentation code in `FgoPet.App`; separate them without changing public namespaces at the first stable point.
