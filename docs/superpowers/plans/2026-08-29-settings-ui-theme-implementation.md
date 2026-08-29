# Settings UI Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace fragmented Phase 3 settings windows with one embedded `SettingsWindow` containing user profile, personalization, role-package management/details, AI model connection, conversation/memory, privacy, and theme pages, then publish a Release acceptance package.

**Architecture:** `SettingsWindow` is the only user-facing configuration shell. Its left navigation changes the right content in place; the `角色包` page opens package details in the same content region. Existing provider, servant, memory, export, deletion, and credential services remain responsible for business behavior and are hosted by embedded page view models. Tray and portrait context menus expose only `设置` for configuration.

**Tech Stack:** .NET 8, C# 12, WPF XAML, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, xUnit, SQLite, JSON settings, Windows Credential Manager.

**Spec:** `docs/superpowers/specs/2026-08-29-settings-ui-theme-design.md`

## Global Constraints

- Modern Gray is the default; FGO Light is optional and both themes use shared semantic resources.
- All settings pages and role-package details are embedded in one `SettingsWindow`; only confirmations, native file selection, and optional image zoom use temporary modal surfaces.
- Every top-level setting and package-detail subsection has a bundled 16–18 px vector icon with idle, selected, focused, and disabled states; do not use Emoji or a third-party icon font.
- The top-level settings sections are `用户资料`, `个性化`, `角色包`, `AI 模型与连接`, `对话与记忆`, `数据与隐私`, and `主题`.
- Role-package input uses `.fgopetpack`; package-defined settings are declarative and cannot inject arbitrary WPF controls or executable behavior.
- `servant_id` is the stable owner for user address and memory data; appearance changes may change Persona/Knowledge but never move user-owned data.
- API keys remain in Windows Credential Manager; provider metadata and global profile/personalization remain in JSON; conversation and memory records remain in SQLite.
- Tray and portrait context menus expose `设置`, not direct `模型连接` or `从者库` destinations.
- The runtime dialogue panel receives a visual redesign in this phase, but its existing message stream, send/stop/new-conversation behavior, state machine, four header columns, hit-testing, drag behavior, and DPI metrics remain unchanged.
- Existing portrait, focus, timeline, hit-testing, bond, and dialogue behavior remains unchanged except for explicit missing-model navigation.
- Release remains framework-dependent `win-x64` and requires the .NET 8 Desktop Runtime.
- Preserve unrelated working-tree changes and stage only files named by the current task.

## File map

- `src/FgoPet.Core/Settings/AppTheme.cs`: persisted theme enum and stable identifiers.
- `src/FgoPet.Core/Settings/UserProfile.cs`: optional global display name contract.
- `src/FgoPet.Core/Settings/AppSettings.cs`: theme, profile, and package-setting fields with backward-compatible defaults.
- `src/FgoPet.Core/Packs/PackContracts.cs`: validated declarative package-setting definitions.
- `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`: safe JSON mapping for new fields.
- `src/FgoPet.App/Theming/ThemeService.cs`: applies one semantic theme dictionary and persists the selected theme.
- `src/FgoPet.App/Themes/ModernGray.xaml`, `FgoLight.xaml`, `SettingsControls.xaml`, `SettingsIcons.xaml`: shared visual and icon resources.
- `src/FgoPet.App/Settings/SettingsWindow.xaml(.cs)`: the only configuration shell and embedded route host.
- `src/FgoPet.App/Settings/SettingsViewModel.cs`: top-level navigation and package-detail route state.
- `src/FgoPet.App/Settings/*Page.xaml(.cs)`: profile, personalization, package, model, memory/privacy, and theme page surfaces.
- `src/FgoPet.App/Panels/AttachedPanelView.xaml(.cs)`: runtime dialogue panel visual surface, preserving the existing panel state and interaction contract.
- `src/FgoPet.App/Dialogue/ConversationViewModel.cs`, `ConversationTurnViewModel.cs`: presentation state and configuration-action contract for the runtime dialogue surface.
- `tests/FgoPet.Windows.Tests/Panels/DialoguePanelIntegrationTests.cs`: runtime dialogue visual-surface and named-control coverage.
- `src/FgoPet.App/Bootstrap/DesktopAppUi.cs`, `DesktopAppShell.cs`, `Tray/TrayService.cs`: settings-only configuration entry points and clean-start route.
- Existing `ModelConnectionWindow`, `ServantLibraryWindow`, and `MemoryWindow` are migrated into page controls and removed as top-level settings windows after their behavior is covered by embedded tests.

---

### Task 1: Persist profile, theme, and package settings safely

**Files:**
- Create: `src/FgoPet.Core/Settings/AppTheme.cs`
- Create: `src/FgoPet.Core/Settings/UserProfile.cs`
- Modify: `src/FgoPet.Core/Settings/AppSettings.cs`
- Modify: `src/FgoPet.Core/Packs/PackContracts.cs`
- Modify: `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Modify: `src/FgoPet.App/Privacy/UserDataDeletionService.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Settings/JsonSettingsTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Packs/PackManifestTests.cs`
- Test: `tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs`
- Test: `tests/FgoPet.App.Tests/Privacy/UserDataControlTests.cs`

**Interfaces:** Produce `AppTheme.ModernGray`/`AppTheme.FgoLight`, `UserProfile(string? DisplayName)`, optional `AppSettings.Theme`, `AppSettings.UserProfile`, and `AppSettings.PackageSettings`. Preserve the four-argument `AppSettings` constructor and every existing field when activating a servant.

- [ ] **Step 1: Write failing tests for defaults, round trips, unknown themes, profile data, package settings, and servant activation preservation.**

```csharp
[Fact]
public void Save_then_load_roundtrips_profile_theme_and_package_settings()
{
    var settings = AppSettings.Defaults with
    {
        Theme = AppTheme.FgoLight,
        UserProfile = new UserProfile("xqj"),
        PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["mash_kyrielight"] = new Dictionary<string, string> { ["show_status"] = "true" },
        },
    };
    _store.Save(settings);
    var loaded = _store.Load();
    Assert.Equal(AppTheme.FgoLight, loaded.Theme);
    Assert.Equal("xqj", loaded.UserProfile!.DisplayName);
    Assert.Equal("true", loaded.PackageSettings["mash_kyrielight"]["show_status"]);
}
```

- [ ] **Step 2: Run `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~JsonSettingsTests` and verify RED because the new fields do not exist.**
- [ ] **Step 3: Add the enum, profile record, optional settings fields, JSON DTOs, and explicit snake-case mappings. Unknown theme IDs fall back to Modern Gray without quarantining an otherwise valid settings file.**
- [ ] **Step 4: Add `PackSettingDefinition` for `toggle`, `choice`, and `text`; validate key/label length, options, default values, and reject unsupported values before rendering.**
- [ ] **Step 5: Change servant activation to `_settings.Save(_settings.Load() with { Selection = selection })`; test that theme, profile, model metadata, memory flag, servant preferences, and package settings survive activation.**
- [ ] **Step 6: Extend all-data deletion to clear profile/package settings and model metadata while preserving Phase 2 focus/bond history; test that credentials remain handled only through `ICredentialStore`.**
- [ ] **Step 7: Run Core, Infrastructure, and App focused tests and commit with `git add src/FgoPet.Core/Settings src/FgoPet.Core/Packs/PackContracts.cs src/FgoPet.Infrastructure/Settings src/FgoPet.App/Servants/ServantLibraryViewModel.cs src/FgoPet.App/Privacy tests/FgoPet.Core.Tests tests/FgoPet.Infrastructure.Tests/Settings tests/FgoPet.Infrastructure.Tests/Packs tests/FgoPet.App.Tests/Servants tests/FgoPet.App.Tests/Privacy; git commit -m "feat: persist settings profile and package state"`.**

### Task 2: Add shared theme, control, and icon resources

**Files:**
- Create: `src/FgoPet.App/Theming/ThemeService.cs`
- Create: `src/FgoPet.App/Themes/ModernGray.xaml`
- Create: `src/FgoPet.App/Themes/FgoLight.xaml`
- Create: `src/FgoPet.App/Themes/SettingsControls.xaml`
- Create: `src/FgoPet.App/Themes/SettingsIcons.xaml`
- Modify: `src/FgoPet.App/App.xaml`
- Modify: `src/FgoPet.App/App.xaml.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Test: `tests/FgoPet.Windows.Tests/Theming/ThemeServiceTests.cs`

**Interfaces:** `ThemeService.CurrentTheme`, `StatusText`, `Initialize()`, `Select(AppTheme)`, and `ThemeChanged` apply only the marked theme dictionary. Resources include semantic backgrounds, surfaces, text, sidebar, accent, borders, warning/danger brushes, shared controls, and icon geometries for every navigation item.

- [ ] **Step 1: Write failing STA tests for saved-theme initialization, immediate selection/persistence, resource-load failure, and save failure.**
- [ ] **Step 2: Run `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ThemeServiceTests` and verify RED because the service/resources do not exist.**
- [ ] **Step 3: Implement `ThemeService` so it loads the next dictionary before removing the previous marked dictionary, retains the previous theme on resource failure, and keeps the applied theme while reporting save failure.**
- [ ] **Step 4: Add Modern Gray and FGO Light dictionaries with identical semantic keys and `SettingsIcons.xaml` with application-owned `Geometry` resources for profile, personalization, role package, connection, conversation, privacy, theme, appearance, address, and package info.**
- [ ] **Step 5: Add keyed styles for navigation, cards, primary/secondary/danger buttons, text boxes, combo boxes, page headers, captions, status, and theme choices; use `DynamicResource` for theme-owned values.**
- [ ] **Step 6: Merge resources in `App.xaml` and initialize the service after DI construction and before visible app windows are created. Run tests/build and commit with `git add src/FgoPet.App/Theming src/FgoPet.App/Themes src/FgoPet.App/App.xaml src/FgoPet.App/App.xaml.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.Windows.Tests/Theming; git commit -m "feat: add settings theme and icon resources"`.**

### Task 3: Define one embedded settings shell and navigation model

**Files:**
- Create: `src/FgoPet.App/Settings/SettingsSection.cs`
- Create: `src/FgoPet.App/Settings/SettingsNavigationItem.cs`
- Create: `src/FgoPet.App/Settings/PackageDetailRoute.cs`
- Create: `src/FgoPet.App/Settings/SettingsViewModel.cs`
- Create: `src/FgoPet.App/Settings/SettingsWindow.xaml`
- Create: `src/FgoPet.App/Settings/SettingsWindow.xaml.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppUi.cs`
- Test: `tests/FgoPet.App.Tests/Settings/SettingsViewModelTests.cs`
- Test: `tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs`

**Interfaces:** `SettingsSection` contains `UserProfile`, `Personalization`, `RolePackages`, `ModelConnection`, `ConversationMemory`, `Privacy`, and `Theme`. `SettingsViewModel` exposes `NavigationItems`, `SelectedSection`, `PackageDetail`, `Select`, `OpenPackageCommand`, and `BackToPackagesCommand`. `SettingsWindow(SettingsViewModel, page dependencies)` contains named `SettingsNavigation` and `SettingsContent`; `DesktopAppUi.ShowSettings(SettingsSection? section = null)` shows the singleton shell.

- [ ] **Step 1: Write failing tests asserting exact labels/icon keys, section changes without new windows, package route/back behavior, title `设置`, and one shell instance.**
- [ ] **Step 2: Run the focused App and Windows filters and verify RED because the navigation model and shell do not exist.**
- [ ] **Step 3: Implement observable navigation state and a fixed icon+label sidebar; clicking a title changes the right content `ContentControl` in place.**
- [ ] **Step 4: Add a page header, scrollable content region, package breadcrumb/back action, and same-window route state that preserves selected package and non-secret page input for the session.**
- [ ] **Step 5: Run focused tests and commit with `git add src/FgoPet.App/Settings src/FgoPet.App/Bootstrap/DesktopAppUi.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.App.Tests/Settings tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs; git commit -m "feat: add unified embedded settings shell"`.**

### Task 4: Implement profile, personalization, and theme pages

**Files:**
- Create: `src/FgoPet.App/Settings/UserProfileViewModel.cs`
- Create: `src/FgoPet.App/Settings/UserProfilePage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/PersonalizationViewModel.cs`
- Create: `src/FgoPet.App/Settings/PersonalizationPage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/ThemePage.xaml(.cs)`
- Test: `tests/FgoPet.App.Tests/Settings/UserProfileViewModelTests.cs`
- Test: `tests/FgoPet.App.Tests/Settings/PersonalizationViewModelTests.cs`

**Interfaces:** `UserProfileViewModel.DisplayName`, `SaveCommand`, and `ResetCommand` modify only global profile data. `PersonalizationViewModel` exposes theme, scale, topmost, auto-collapse, and reset behavior. `ThemePage` presents Modern Gray/FGO Light preview cards and applies selection immediately.

- [ ] **Step 1: Write failing tests that saving a display name leaves `ServantPreferences[servant_id]` unchanged and that scale/topmost/auto-collapse/theme values round-trip.**
- [ ] **Step 2: Run the focused filters and verify RED.**
- [ ] **Step 3: Implement profile and personalization page models with validation, saved-status text, profile-only explanation, theme service integration, and restore-defaults. Keep startup/notification/animation settings out of this phase.**
- [ ] **Step 4: Build the XAML pages using shared cards/styles/icons; verify keyboard focus, disabled states, and no profile nickname injection into servant address resolution.**
- [ ] **Step 5: Run focused tests and commit with `git add src/FgoPet.App/Settings tests/FgoPet.App.Tests/Settings/UserProfileViewModelTests.cs tests/FgoPet.App.Tests/Settings/PersonalizationViewModelTests.cs; git commit -m "feat: add profile personalization and theme pages"`.**

### Task 5: Embed role-package management, preview, and details

**Files:**
- Create: `src/FgoPet.App/Settings/RolePackagesPage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/RolePackageDetailPage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/RolePackageDetailViewModel.cs`
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Modify: `src/FgoPet.App/Servants/ServantCardViewModel.cs`
- Modify: `src/FgoPet.App/Servants/ServantAppearanceItemViewModel.cs`
- Modify: `src/FgoPet.Infrastructure/Packs/FileArtPackageRepository.cs`
- Modify: `src/FgoPet.Core/Packs/PackContracts.cs`
- Test: `tests/FgoPet.App.Tests/Settings/RolePackageDetailViewModelTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Packs/PackManifestTests.cs`
- Test: `tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs`
- Delete after migrated coverage: `src/FgoPet.App/Servants/ServantLibraryWindow.xaml`, `src/FgoPet.App/Servants/ServantLibraryWindow.xaml.cs`

**Interfaces:** The package list shows preview, display name, package ID, version, source, active state, `.fgopetpack` install, scan, folder, and diagnostics. `RolePackageDetailViewModel` exposes package metadata, `PreviewSource`, appearance, package-default/custom address, package settings, activation, and back commands. No package detail or servant-library top-level window is created.

- [ ] **Step 1: Write failing tests for list cards, `打开角色包` route, preview source, version/source metadata, appearance activation, address by `servant_id`, and validated package settings.**
- [ ] **Step 2: Run `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RolePackageDetail` and verify RED.**
- [ ] **Step 3: Implement list/detail pages by adapting existing install/scan/activation behavior; installation remains non-activating and diagnostics remain safe.**
- [ ] **Step 4: Render package preview and detail subsections `从者与外观`, `称呼设置`, and `角色包信息`; use inline subsection navigation, not a new window.**
- [ ] **Step 5: Render only validated toggle/choice/text package settings with application-owned controls; revalidate on package upgrade and fall back to declared defaults with a non-blocking notice.**
- [ ] **Step 6: Migrate existing servant integration assertions into `SettingsWindowIntegrationTests`, run servant/package/Windows tests, remove the obsolete top-level window, and commit with `git add src/FgoPet.App/Settings src/FgoPet.App/Servants src/FgoPet.Infrastructure/Packs src/FgoPet.Core/Packs tests/FgoPet.App.Tests/Settings tests/FgoPet.Infrastructure.Tests/Packs tests/FgoPet.Windows.Tests/Settings; git commit -m "feat: embed role package management and details"`.**

### Task 6: Embed model, conversation/memory, privacy, and data controls

**Files:**
- Create: `src/FgoPet.App/Settings/ModelConnectionPage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/ConversationMemoryPage.xaml(.cs)`
- Create: `src/FgoPet.App/Settings/PrivacyPage.xaml(.cs)`
- Modify: `src/FgoPet.App/Settings/ModelConnectionViewModel.cs`
- Modify: `src/FgoPet.App/Memory/MemoryViewModel.cs`
- Test: `tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs`
- Test: `tests/FgoPet.Windows.Tests/Memory/MemoryWindowIntegrationTests.cs`
- Delete after migrated coverage: `src/FgoPet.App/Settings/ModelConnectionWindow.xaml`, `src/FgoPet.App/Settings/ModelConnectionWindow.xaml.cs`, `src/FgoPet.App/Memory/MemoryWindow.xaml`, `src/FgoPet.App/Memory/MemoryWindow.xaml.cs`

**Interfaces:** Model controls retain provider, API key `PasswordBox`, Base URL, model, refresh, test, save, clear-key, and offline commands. Memory/privacy pages host candidate review, confirmed-memory editing, conversation deletion, export, and all-data deletion without changing service/repository behavior.

- [ ] **Step 1: Write failing integration tests asserting all controls are in `SettingsWindow` and no model/memory settings windows are constructed.**
- [ ] **Step 2: Run the focused Windows filters and verify RED.**
- [ ] **Step 3: Implement embedded pages with shared cards/icons; keep API keys credential-store-only and show provider display name/model ID plus masked key state.**
- [ ] **Step 4: Preserve destructive confirmation and offline behavior; verify one shell survives model → memory → privacy navigation without resetting package selection.**
- [ ] **Step 5: Run App/Windows tests and commit with `git add src/FgoPet.App/Settings src/FgoPet.App/Memory tests/FgoPet.App.Tests/Settings tests/FgoPet.Windows.Tests/Settings tests/FgoPet.Windows.Tests/Memory; git commit -m "feat: embed model memory and privacy settings"`.**

### Task 7: Redesign the runtime dialogue panel UI

**Files:**
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml.cs`
- Modify: `src/FgoPet.App/Dialogue/ConversationViewModel.cs`
- Modify: `src/FgoPet.App/Dialogue/ConversationTurnViewModel.cs`
- Test: `tests/FgoPet.Windows.Tests/Panels/DialoguePanelIntegrationTests.cs`
- Test: `tests/FgoPet.App.Tests/Dialogue/ConversationOrchestratorTests.cs`

**Interfaces:** Preserve `DialogueContent`, `DialogueInputBox`, `SendDialogueButton`, `StopDialogueButton`, and `NewConversationButton`. Add named presentation surfaces for the empty state, provider/model status badges, message list, composer, and missing-model `DialogueSettingsButton`. The view model exposes enough presentation state for an empty/configuration-required view and raises the existing settings route without directly owning a settings window.

- [ ] **Step 1: Write failing integration/view-model tests for the new named surfaces, empty state, provider/model badges, configuration-required state, settings action, and user/assistant message presentation. Keep the existing collapsed-state and four-header assertions.**
- [ ] **Step 2: Run `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DialoguePanelIntegrationTests` and the focused App dialogue tests; verify RED because the presentation contract and controls do not exist.**
- [ ] **Step 3: Add presentation-only view-model properties/action wiring and redesign the XAML with a clear header/status strip, empty state, distinct user/assistant message bubbles, scrollable message history, and a grouped composer/action area. Keep API-key/model configuration behind the settings route.**
- [ ] **Step 4: Verify send, stop, new conversation, missing-model navigation, servant isolation, panel state transitions, four header columns, pointer hit-testing, drag behavior, and DPI metrics. Do not change the stream/orchestrator/repository contract.**
- [ ] **Step 5: Run focused App/Windows tests and commit with `git add src/FgoPet.App/Panels/AttachedPanelView.xaml src/FgoPet.App/Panels/AttachedPanelView.xaml.cs src/FgoPet.App/Dialogue/ConversationViewModel.cs src/FgoPet.App/Dialogue/ConversationTurnViewModel.cs tests/FgoPet.Windows.Tests/Panels/DialoguePanelIntegrationTests.cs tests/FgoPet.App.Tests/Dialogue/ConversationOrchestratorTests.cs; git commit -m "feat: redesign runtime dialogue panel"`.**

### Task 8: Remove direct menu entries and connect missing-model navigation

**Files:**
- Modify: `src/FgoPet.App/Tray/TrayService.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppUi.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml.cs`
- Modify: `src/FgoPet.App/Dialogue/ConversationViewModel.cs`
- Test: `tests/FgoPet.App.Tests/Bootstrap/DesktopAppShellTests.cs`
- Test: `tests/FgoPet.App.Tests/Dialogue/ConversationOrchestratorTests.cs`
- Test: `tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs`

**Interfaces:** Tray and portrait menus contain `设置`, not direct `模型连接`/`从者库`. `ShowSettings(SettingsSection? section)` supports the dialogue `去设置` action. No-pack startup opens `设置 > 角色包`; a valid role starts offline without forcing model configuration.

- [ ] **Step 1: Write failing tests for exact menu labels, no direct model/library items, no-pack settings route, and model target section.**
- [ ] **Step 2: Run App/Windows focused tests and verify RED against current callbacks.**
- [ ] **Step 3: Remove direct callbacks, route configuration through the singleton settings shell, and retain `打开角色包目录` only if treated as a filesystem shortcut rather than a settings page.**
- [ ] **Step 4: Run focused tests and commit with `git add src/FgoPet.App/Tray src/FgoPet.App/Bootstrap src/FgoPet.App/Panels src/FgoPet.App/Dialogue tests/FgoPet.App.Tests/Bootstrap tests/FgoPet.App.Tests/Dialogue tests/FgoPet.Windows.Tests/Settings; git commit -m "feat: route configuration through embedded settings"`.**

### Task 9: Full verification, manual matrix, documentation, and Release

**Files:**
- Modify: `README.md`
- Modify: `docs/testing/phase2-windows-matrix.md` only for changed settings paths
- Create: `docs/reports/2026-08-29-phase3-settings-handoff.md`
- Create: `scripts/test-phase3-settings.ps1`

- [ ] **Step 1: Add a serial acceptance script covering profile/personalization persistence, package install/detail/preview, appearance/address, provider/model status, offline behavior, memory/privacy, icons/themes, menu exclusions, and secret/path redaction.**
- [ ] **Step 2: Run the four project Release test commands serially:** `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore`; `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore`; `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore`; `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore`. Expected: all pass.
- [ ] **Step 3: Run `dotnet build FgoPet.sln -c Release --no-restore`; inspect XAML/resource errors and verify no runtime surface regressions.**
- [ ] **Step 4: Manually verify with a clean Windows profile at 100%, 150%, and 200% scaling: startup `设置 > 角色包`, `.fgopetpack` install, `打开角色包`, preview, appearance/address, all embedded sections, icon focus/disabled states, redesigned runtime dialogue empty/configured/streaming/error states, model offline path, and no extra top-level settings windows.**
- [ ] **Step 5: Publish with `dotnet publish src/FgoPet.App/FgoPet.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/release/FgoPet-win-x64`; verify `FgoPet.App.exe`, `.runtimeconfig.json`, and `.deps.json`.**
- [ ] **Step 6: Record automated/manual results and the Release path/runtime prerequisite in the handoff report; commit only planned documentation/script files. Do not push unless separately requested.**
