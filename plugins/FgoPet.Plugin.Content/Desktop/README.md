# character

角色模块拥有角色包与个性化设置 WPF UI，依赖 Kernel、Content 和 UiSdk。

## Current ownership

- `FgoPet.Kernel` owns active role identity, user profile and character preferences, neutral semantic presentation, and process/context lifecycle.
- `FgoPet.Plugin.Content` owns package validation and installation, persona/knowledge reads, event feedback, and the sole `RoleActivationService`.
- `FgoPet.Portrait.Static` owns portrait snapshots, its controller, and WPF rendering. Kernel exposes the semantic `IPortraitBackend`/`IPortraitController` boundary. UiSdk publishes the desktop `IPortraitSurface`/immutable frame boundary; Shell hosts content and geometry without static renderer snapshots.
- This module owns character package/personalization Web settings pages and their metadata.
- `PersonalizationWebPage` also owns the Personalization commands for the shared Settings Web root. It delegates preference writes to `PersonalizationViewModel`, theme changes to `ThemeService`, and role metadata to `ServantLibraryViewModel`; each root host receives an independent page session.
- `ThemeWebPage` owns the Theme commands for the shared Settings Web root and delegates selection, effective appearance, status, and persistence to the existing `ThemeService`; each root host receives an independent page session.
- `RolePackagesWebPage` owns the RolePackages catalog/detail commands for the shared Settings Web root and delegates package validation, installation, activation, preferences, and package-setting persistence to the existing library/detail view models; each root host keeps its own route and drafts.

## Safety

Role packages are untrusted input. Preserve integrity/version validation and prevent archive extraction from escaping its destination. Preferences and profile values are user data.

## Validation

Use the relevant package, portrait, and character UI checks when changing this module. Package installation and validation require malicious-archive coverage.
