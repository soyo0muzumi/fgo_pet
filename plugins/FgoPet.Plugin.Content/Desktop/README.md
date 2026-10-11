# character

角色模块拥有角色包、个性化与主题设置 Web UI，依赖 Kernel、Content 和 UiSdk。

## Current ownership

- `FgoPet.Kernel` owns active role identity, user profile and character preferences, neutral semantic presentation, and process/context lifecycle.
- `FgoPet.Plugin.Content` owns package validation and installation, persona/knowledge reads, event feedback, and the sole `RoleActivationService`.
- `FgoPet.Portrait.Static` owns portrait snapshots, its controller, and WPF rendering. Kernel exposes the semantic `IPortraitBackend`/`IPortraitController` boundary. UiSdk publishes the desktop `IPortraitSurface`/immutable frame boundary; Shell hosts content and geometry without static renderer snapshots.
- This module owns character package/personalization Web settings pages and their metadata.
- `PersonalizationWebPage` also owns the Personalization commands for the shared Settings Web root. It delegates preference writes to `PersonalizationViewModel`, theme changes to `ThemeService`, and role metadata to `ServantLibraryViewModel`; each root host receives an independent page session.
- The personalization presentation uses English localization keys for its controls and local feedback. The Margin root supplies an optional presentation-only overview flag: Companion keeps actual character/size/presence controls and places reset under More preferences; appearance is reachable through the existing Theme owner in App. This flag does not change stored preferences or command validation.
- `ThemeWebPage` owns the Theme commands for the shared Settings Web root and delegates selection, effective appearance, status, and persistence to the existing `ThemeService`; each root host receives an independent page session.
- `RolePackagesWebPage` owns the RolePackages catalog/detail commands for the shared Settings Web root and delegates package validation, installation, activation, preferences, and package-setting persistence to the existing library/detail view models; each root host keeps its own route and drafts.
- Role-package and Appearance presentation uses English localization keys and Margin paper controls. Character names use the shared serif font; package-provided text and authoritative status remain plain text. The existing catalog/detail sections, file picker, uninstall confirmation and immediate theme selection keep their original owner commands. Theme swatches describe the existing application themes; the Margin shell retains its paper palette.

## Safety

Role packages are untrusted input. Preserve integrity/version validation and prevent archive extraction from escaping its destination. Preferences and profile values are user data.

## Validation

Use the relevant package, portrait, and character UI checks when changing this module. Package installation and validation require malicious-archive coverage.

`RolePackagesWebTests` and `ThemeWebTests` render synthetic data through the production Settings root, check navigation, theme updates and narrow-window layout. Optional `FGO_PET_MARGIN_CHARACTER_CAPTURE_DIR` captures only those fixture WebViews. These checks do not replace real package, desktop, DPI or accessibility acceptance.

Character details use the Settings heading's Back through the module's optional `goBack` hook; the owner still flushes drafts and returns to its catalog. The catalog folder icon beside search invokes the existing native package picker without changing search, installation or package validation.
