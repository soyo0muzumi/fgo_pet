# Modules

## Ownership map

Business modules: focus, dialogue, memory, todo, servant-packs, agent-integration, settings-privacy

Application support: desktop-shell

Shared support outside modules/: ui-foundation, foundation

Validation support outside modules/: integration-tests

Migration status:

- `agent-integration` — Migrated. Production projects are under `modules/agent-integration/src/`; unit-test projects are under `modules/agent-integration/tests/`. Supporting Agent/archive code remains at its documented legacy paths.
- `desktop-shell` — Planned. Current paths: `src/FgoPet.App/{Bootstrap,Lifetime,Main,Panels,Portraits,Runtime,Tray,Windowing}` and shell-only tests in `tests/FgoPet.App.Tests` and `tests/FgoPet.Windows.Tests`.
- `dialogue` — Planned. Current paths: `src/FgoPet.Core/Dialogue`, `src/FgoPet.App/{Dialogue,Providers,Conversation}`, and `src/FgoPet.Infrastructure/{Dialogue,Providers}` with corresponding legacy tests.
- `focus` — Planned. Current paths: `src/FgoPet.Core/{Focus,Bond,Timeline}`, `src/FgoPet.App/Focus`, and `src/FgoPet.Infrastructure/{Focus,Bond,Timeline}` with corresponding legacy tests.
- `memory` — Planned. Current paths: `src/FgoPet.Core/Memory`, `src/FgoPet.App/Memory`, `src/FgoPet.Infrastructure/Memory`, `ConversationSummaryService`, and memory-management pages/view models with corresponding legacy tests.
- `servant-packs` — Planned. Current paths: `src/FgoPet.Core/{Packs,Portraits}`, `src/FgoPet.App/Servants`, role-pack/servant Settings pages, and `src/FgoPet.Infrastructure/Packs` with corresponding legacy tests.
- `settings-privacy` — Planned. Current paths: `src/FgoPet.Core/{Settings,Backup}`, `src/FgoPet.App/{Settings,Privacy}`, `src/FgoPet.Infrastructure/{Settings,Secrets,Backup}`, and `src/FgoPet.Infrastructure/Persistence/RuntimeDatabase*` with corresponding legacy tests.
- `todo` — Planned. Current paths: `src/FgoPet.Core/Todo`, Todo services/view models/views in `src/FgoPet.App`, and `src/FgoPet.Infrastructure/Persistence/SqliteTodoRepository.cs` with corresponding legacy tests.
- `foundation` — Planned. Current paths: candidate shared infrastructure in `src/FgoPet.Infrastructure/{FileSystem,Json,Diagnostics}` and shared geometry/event/database infrastructure with corresponding legacy tests.
- `ui-foundation` — Planned. Current paths: candidate shared resources in `src/FgoPet.App/{Themes,Theming}`, `SettingsControls.xaml`, shared icons, and business-neutral controls with corresponding legacy tests.
- `integration-tests` — Planned. Current paths: `tests/FgoPet.EndToEnd.Tests` and cross-module portions of `tests/FgoPet.Windows.Tests`.

## Dependency direction

The intended direction is `desktop-shell -> business modules -> foundation`. Modules that contain user-interface code may depend on `ui-foundation`. The specific cross-module edges are `dialogue -> memory` and `todo -> agent-integration`. `foundation` and `ui-foundation` do not depend on feature modules or `desktop-shell`.

Each module README is the authoritative ownership and validation index for its approved root. The code remains in its current `src/` and `tests/` locations until the migration slice that moves it.
