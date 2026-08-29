# Settings UI Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two plain settings windows with a coherent sidebar-based settings experience and two persisted, instantly switchable themes.

**Architecture:** Persist a strongly typed `AppTheme` preference in the existing settings store, then let one WPF `ThemeService` swap semantic color dictionaries while shared component styles remain stable. `ServantLibraryWindow` becomes the primary sidebar shell and owns the `Theme / 主题` destination; `ModelConnectionWindow` remains independent and automatically follows application resources.

**Tech Stack:** .NET 8, C# 12, WPF XAML, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, xUnit

**Spec:** `docs/superpowers/specs/2026-08-29-settings-ui-theme-design.md`

## Global Constraints

- Modern Gray is the default; FGO Light is optional.
- Theme selection appears only as the `Theme / 主题` sidebar destination in the primary settings window.
- Theme scope is limited to `ServantLibraryWindow` and `ModelConnectionWindow`.
- Preserve `ProviderComboBox`, `ApiKeyBox`, and `ModelTextBox`, the independent model window title, and tray behavior.
- Keep JSON schema version 2 and continue reading schema versions 1 and 2.
- Do not add a third-party WPF theme framework.
- Do not change portrait, attached panel, focus, timeline, or memory-window styling.
- Preserve all unrelated working-tree changes and stage only files named by the current task.

## File map

- `src/FgoPet.Core/Settings/AppTheme.cs`: persisted theme values and stable identifiers.
- `src/FgoPet.Core/Settings/AppSettings.cs`: adds the defaulted theme preference.
- `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`: serializes and safely parses the optional theme field.
- `src/FgoPet.App/Theming/ThemeService.cs`: loads, applies, and persists one application theme.
- `src/FgoPet.App/Themes/ModernGray.xaml`: Modern Gray semantic brushes.
- `src/FgoPet.App/Themes/FgoLight.xaml`: FGO Light semantic brushes.
- `src/FgoPet.App/Themes/SettingsControls.xaml`: shared settings-window typography and control styles.
- `src/FgoPet.App/App.xaml`: loads the default theme and shared settings styles.
- `src/FgoPet.App/App.xaml.cs`: initializes the persisted theme before the app shell creates visible windows.
- `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`: registers `ThemeService` and supplies settings-window navigation callbacks.
- `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`: exposes a filtered servant list while preserving all settings fields on activation.
- `src/FgoPet.App/Servants/ServantLibraryWindow.xaml(.cs)`: sidebar shell, page navigation, theme cards, and window-launch actions.
- `src/FgoPet.App/Settings/ModelConnectionWindow.xaml`: regrouped themed connection form.
- Tests stay in their matching existing Core/Infrastructure/App/Windows test projects.

---

### Task 1: Persist a safe theme preference

**Files:**
- Create: `src/FgoPet.Core/Settings/AppTheme.cs`
- Modify: `src/FgoPet.Core/Settings/AppSettings.cs`
- Modify: `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Settings/JsonSettingsTests.cs`
- Modify: `tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs`

**Interfaces:**
- Produces: `AppTheme.ModernGray`, `AppTheme.FgoLight`, `AppSettings.Theme`.
- Produces stable JSON values `modern_gray` and `fgo_light` under property `theme`.
- Preserves every `AppSettings` init property when activating a servant.

- [ ] **Step 1: Write failing settings tests**

Add these assertions/tests to `JsonSettingsTests.cs`:

```csharp
Assert.Equal(AppTheme.ModernGray, _store.Load().Theme);

[Theory]
[InlineData(AppTheme.ModernGray)]
[InlineData(AppTheme.FgoLight)]
public void Save_then_Load_roundtrips_theme(AppTheme theme)
{
    _store.Save(AppSettings.Defaults with { Theme = theme });
    Assert.Equal(theme, _store.Load().Theme);
}

[Fact]
public void Load_unknown_theme_falls_back_without_quarantining_settings()
{
    File.WriteAllText(_store.Location,
        """{"schema_version":2,"scale":0.5,"topmost":true,"auto_collapse":true,"theme":"future_theme"}""");

    var loaded = _store.Load();

    Assert.Equal(AppTheme.ModernGray, loaded.Theme);
    Assert.True(File.Exists(_store.Location));
}
```

Extend `Save_then_Load_roundtrips_every_field` with `Theme = AppTheme.FgoLight` and `Assert.Equal(saved.Theme, loaded.Theme)`.

- [ ] **Step 2: Write a failing regression test for servant activation**

In `ServantLibraryViewModelTests.cs`, configure the fake settings with FGO Light, model connection, memory disabled, and one servant preference. Activate an appearance and assert the saved value retains them:

```csharp
Assert.Equal(AppTheme.FgoLight, settings.Saved.Last().Theme);
Assert.NotNull(settings.Saved.Last().ModelConnection);
Assert.False(settings.Saved.Last().MemoryEnabled);
Assert.Single(settings.Saved.Last().ServantPreferences);
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter FullyQualifiedName~JsonSettingsTests
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~ServantLibraryViewModelTests
```

Expected: compilation fails because `AppTheme` and `AppSettings.Theme` do not exist, or the preservation assertions fail.

- [ ] **Step 4: Add the theme model and JSON mapping**

Create `AppTheme.cs`:

```csharp
namespace FgoPet.Core.Settings;

public enum AppTheme
{
    ModernGray,
    FgoLight,
}
```

Add to `AppSettings`:

```csharp
public AppTheme Theme { get; init; } = AppTheme.ModernGray;
```

Add `Theme` to the DTO without changing `SchemaVersion`:

```csharp
[JsonPropertyName("theme")]
public string? Theme { get; init; }
```

Use explicit mappings:

```csharp
private static AppTheme ParseTheme(string? value) => value switch
{
    "fgo_light" => AppTheme.FgoLight,
    _ => AppTheme.ModernGray,
};

private static string SerializeTheme(AppTheme value) => value switch
{
    AppTheme.FgoLight => "fgo_light",
    _ => "modern_gray",
};
```

Set `Theme = ParseTheme(dto.Theme)` in `Load` and `Theme = SerializeTheme(settings.Theme)` in `Save`.

- [ ] **Step 5: Preserve settings on servant activation**

Replace construction of a new `AppSettings` in `ActivateAsync` with:

```csharp
var settings = _settings.Load();
_settings.Save(settings with { Selection = selection });
```

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run the two commands from Step 3. Expected: all selected tests pass.

- [ ] **Step 7: Commit the persistence slice**

```powershell
git add src/FgoPet.Core/Settings/AppTheme.cs src/FgoPet.Core/Settings/AppSettings.cs src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs src/FgoPet.App/Servants/ServantLibraryViewModel.cs tests/FgoPet.Infrastructure.Tests/Settings/JsonSettingsTests.cs tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs
git commit -m "feat: persist settings theme preference"
```

---

### Task 2: Add the shared WPF theme service and resources

**Files:**
- Create: `src/FgoPet.App/Theming/ThemeService.cs`
- Create: `src/FgoPet.App/Themes/ModernGray.xaml`
- Create: `src/FgoPet.App/Themes/FgoLight.xaml`
- Create: `src/FgoPet.App/Themes/SettingsControls.xaml`
- Modify: `src/FgoPet.App/App.xaml`
- Modify: `src/FgoPet.App/App.xaml.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Create: `tests/FgoPet.Windows.Tests/Theming/ThemeServiceTests.cs`

**Interfaces:**
- Consumes: `AppTheme` and `AppSettings.Theme` from Task 1.
- Produces: `ThemeService.CurrentTheme`, `ThemeService.StatusText`, `ThemeService.Initialize()`, and `ThemeService.Select(AppTheme)`.
- Produces dynamic resources such as `SettingsWindowBackgroundBrush`, `SettingsSurfaceBrush`, `SettingsTextBrush`, `SettingsMutedTextBrush`, `SettingsAccentBrush`, `SettingsBorderBrush`, `SettingsDangerBrush`, and `SettingsWarningBrush`.

- [ ] **Step 1: Write failing STA tests for theme application**

Create `ThemeServiceTests.cs` with an in-memory settings store and resource loader:

```csharp
[Fact]
public void Initialize_uses_saved_theme_and_replaces_only_the_theme_dictionary()
{
    StaRun(() =>
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { ["SharedMarker"] = true });
        var settings = new Settings(AppSettings.Defaults with { Theme = AppTheme.FgoLight });
        var service = new ThemeService(resources, settings, ThemeDictionary);

        service.Initialize();

        Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
        Assert.True(resources.Contains("SharedMarker"));
        Assert.Equal("fgo", resources["ThemeMarker"]);
    });
}

[Fact]
public void Select_applies_and_persists_theme_immediately()
{
    StaRun(() =>
    {
        var settings = new Settings(AppSettings.Defaults);
        var service = new ThemeService(new ResourceDictionary(), settings, ThemeDictionary);
        service.Initialize();

        Assert.True(service.Select(AppTheme.FgoLight));
        Assert.Equal(AppTheme.FgoLight, settings.Current.Theme);
    });
}

[Fact]
public void Failed_save_keeps_applied_theme_and_reports_status()
{
    StaRun(() =>
    {
        var settings = new Settings(AppSettings.Defaults) { ThrowOnSave = true };
        var service = new ThemeService(new ResourceDictionary(), settings, ThemeDictionary);
        service.Initialize();

        Assert.False(service.Select(AppTheme.FgoLight));
        Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
        Assert.Contains("保存失败", service.StatusText);
    });
}

[Fact]
public void Failed_resource_load_keeps_the_last_successful_theme()
{
    StaRun(() =>
    {
        var settings = new Settings(AppSettings.Defaults);
        var service = new ThemeService(
            new ResourceDictionary(),
            settings,
            theme => theme == AppTheme.FgoLight
                ? throw new IOException("missing theme")
                : ThemeDictionary(theme));
        service.Initialize();

        Assert.False(service.Select(AppTheme.FgoLight));
        Assert.Equal(AppTheme.ModernGray, service.CurrentTheme);
        Assert.Contains("加载失败", service.StatusText);
    });
}
```

Use `ThemeDictionary` to return a dictionary with `ThemeMarker` set to `modern` or `fgo`; use the same `StaRun` pattern as existing Windows integration tests.

- [ ] **Step 2: Run the theme-service tests and verify RED**

Run:

```powershell
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter FullyQualifiedName~ThemeServiceTests
```

Expected: compilation fails because `ThemeService` does not exist.

- [ ] **Step 3: Implement `ThemeService`**

Implement this public surface:

```csharp
public sealed class ThemeService
{
    public ThemeService(
        ResourceDictionary resources,
        IAppSettingsStore settings,
        Func<AppTheme, ResourceDictionary>? loadDictionary = null);

    public AppTheme CurrentTheme { get; private set; } = AppTheme.ModernGray;
    public string StatusText { get; private set; } = string.Empty;
    public event EventHandler? ThemeChanged;
    public void Initialize();
    public bool Select(AppTheme theme);
}
```

The production loader uses pack URIs:

```csharp
var file = theme == AppTheme.FgoLight ? "FgoLight.xaml" : "ModernGray.xaml";
return new ResourceDictionary
{
    Source = new Uri($"/FgoPet.App;component/Themes/{file}", UriKind.Relative),
};
```

Tag the active dictionary with `ThemeDictionaryMarker` and replace only the prior marked dictionary. Load the new dictionary before removing the old one. On resource-load failure, retain the previous dictionary and return `false`. On save failure, keep the applied theme, set `StatusText = "主题已应用，但保存失败。"`, and return `false`.

- [ ] **Step 4: Add both semantic palettes**

Create the dictionaries with identical keys. Use these baseline colors:

```xml
<!-- ModernGray.xaml -->
<Color x:Key="SettingsWindowBackgroundColor">#F4F5F7</Color>
<Color x:Key="SettingsSurfaceColor">#FFFFFF</Color>
<Color x:Key="SettingsSidebarColor">#ECEFF3</Color>
<Color x:Key="SettingsTextColor">#202124</Color>
<Color x:Key="SettingsMutedTextColor">#696C73</Color>
<Color x:Key="SettingsAccentColor">#3B6FB6</Color>
<Color x:Key="SettingsBorderColor">#D9DCE2</Color>
<Color x:Key="SettingsDangerColor">#B42318</Color>
<Color x:Key="SettingsWarningColor">#9A6700</Color>
```

```xml
<!-- FgoLight.xaml -->
<Color x:Key="SettingsWindowBackgroundColor">#F4F1EA</Color>
<Color x:Key="SettingsSurfaceColor">#FFFDF8</Color>
<Color x:Key="SettingsSidebarColor">#17253D</Color>
<Color x:Key="SettingsTextColor">#172033</Color>
<Color x:Key="SettingsMutedTextColor">#72798A</Color>
<Color x:Key="SettingsAccentColor">#A98032</Color>
<Color x:Key="SettingsBorderColor">#DED8CA</Color>
<Color x:Key="SettingsDangerColor">#A63A32</Color>
<Color x:Key="SettingsWarningColor">#9A6700</Color>
```

Create matching `SolidColorBrush` resources for every `*Brush` key consumed by XAML. Add a dedicated `SettingsSidebarTextBrush` so FGO Light can use light text without changing content text.

- [ ] **Step 5: Add shared component styles**

In `SettingsControls.xaml`, define keyed styles so unrelated windows are untouched:

```xml
<Style x:Key="SettingsPrimaryButtonStyle" TargetType="Button">
    <Setter Property="Background" Value="{DynamicResource SettingsAccentBrush}" />
    <Setter Property="Foreground" Value="White" />
    <Setter Property="Padding" Value="16,8" />
    <Setter Property="MinHeight" Value="36" />
</Style>

<Style x:Key="SettingsCardStyle" TargetType="Border">
    <Setter Property="Background" Value="{DynamicResource SettingsSurfaceBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource SettingsBorderBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="10" />
    <Setter Property="Padding" Value="20" />
</Style>
```

Also provide keyed styles for secondary, ghost, danger, navigation, text box, combo box, section title, caption, status, and theme-choice controls. Use `DynamicResource` for all theme-owned brushes.

- [ ] **Step 6: Wire resources and startup initialization**

Merge Modern Gray first and shared controls second in `App.xaml`. Register:

```csharp
.AddSingleton(provider => new ThemeService(
    Application.Current.Resources,
    provider.GetRequiredService<IAppSettingsStore>()))
```

In `App.OnStartup`, after building the provider and before resolving `AppStartup`, call:

```csharp
_provider.GetRequiredService<ThemeService>().Initialize();
```

- [ ] **Step 7: Run the focused tests and build**

Run:

```powershell
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter FullyQualifiedName~ThemeServiceTests
dotnet build src/FgoPet.App/FgoPet.App.csproj
```

Expected: all selected tests pass and the WPF project builds without XAML resource errors.

- [ ] **Step 8: Commit the theme infrastructure**

```powershell
git add src/FgoPet.App/Theming/ThemeService.cs src/FgoPet.App/Themes/ModernGray.xaml src/FgoPet.App/Themes/FgoLight.xaml src/FgoPet.App/Themes/SettingsControls.xaml src/FgoPet.App/App.xaml src/FgoPet.App/App.xaml.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.Windows.Tests/Theming/ThemeServiceTests.cs
git commit -m "feat: add shared settings theme service"
```

---

### Task 3: Rebuild the primary settings window as a sidebar shell

**Files:**
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Modify: `src/FgoPet.App/Servants/ServantLibraryWindow.xaml`
- Modify: `src/FgoPet.App/Servants/ServantLibraryWindow.xaml.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs`
- Modify: `tests/FgoPet.Windows.Tests/Servants/ServantLibraryWindowIntegrationTests.cs`

**Interfaces:**
- Consumes: `ThemeService.Select(AppTheme)` and `ThemeService.CurrentTheme` from Task 2.
- Produces: `ServantLibraryViewModel.SearchText`, `ServantLibraryViewModel.FilteredServants`.
- Produces named UI elements `SettingsNavigation`, `ServantList`, `ServantSearchBox`, `ThemeTab`, `ModernGrayThemeChoice`, `FgoLightThemeChoice`, and `ThemeStatusText`.

- [ ] **Step 1: Write failing view-model filtering tests**

Add a test that loads two servants, sets `SearchText`, and verifies filtering is case-insensitive across display name and package id:

```csharp
viewModel.SearchText = "mash";
Assert.Single(viewModel.FilteredServants);
Assert.Equal("preview.mash", viewModel.FilteredServants[0].PackageId);

viewModel.SearchText = "不存在";
Assert.Empty(viewModel.FilteredServants);
```

- [ ] **Step 2: Write failing window-structure tests**

Extend `ServantLibraryWindowIntegrationTests` to construct the window with `ThemeService` and assert:

```csharp
Assert.Equal(6, window.SettingsNavigation.Items.Count);
Assert.Equal("Theme / 主题", window.ThemeTab.Header);
Assert.NotNull(window.ModernGrayThemeChoice);
Assert.NotNull(window.FgoLightThemeChoice);
Assert.Same(viewModel.FilteredServants, window.ServantList.ItemsSource);
```

Raise the FGO theme choice click and assert the fake settings store contains `AppTheme.FgoLight`.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~ServantLibraryViewModelTests
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter FullyQualifiedName~ServantLibraryWindowIntegrationTests
```

Expected: missing filtering properties, constructor mismatch, and missing named controls.

- [ ] **Step 4: Implement filtered servants**

Add generated properties:

```csharp
[ObservableProperty]
private string _searchText = string.Empty;

[ObservableProperty]
private IReadOnlyList<ServantCardViewModel> _filteredServants = Array.Empty<ServantCardViewModel>();
```

Refresh on both source and query changes:

```csharp
partial void OnServantsChanged(IReadOnlyList<ServantCardViewModel> value) => ApplyFilter();
partial void OnSearchTextChanged(string value) => ApplyFilter();

private void ApplyFilter()
{
    var query = SearchText.Trim();
    FilteredServants = query.Length == 0
        ? Servants
        : Servants.Where(card =>
            card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            card.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
}
```

- [ ] **Step 5: Replace the window layout with the sidebar shell**

Use a left-tab `TabControl` named `SettingsNavigation`. Give it exactly six `TabItem` children and bind all existing controls to their original properties/commands. The first page contains `ServantSearchBox` and `ServantList` bound to `FilteredServants`. Place each page content in a `ScrollViewer` and use `SettingsCardStyle` for focused sections.

The `Theme / 主题` page contains two radio-card choices:

```xml
<RadioButton x:Name="ModernGrayThemeChoice"
             GroupName="Theme"
             Tag="ModernGray"
             Content="现代灰色"
             Style="{StaticResource SettingsThemeChoiceStyle}"
             Checked="OnThemeChoiceChecked" />
<RadioButton x:Name="FgoLightThemeChoice"
             GroupName="Theme"
             Tag="FgoLight"
             Content="FGO 轻主题"
             Style="{StaticResource SettingsThemeChoiceStyle}"
             Checked="OnThemeChoiceChecked" />
<TextBlock x:Name="ThemeStatusText" />
```

Set `ThemeStatusText.Text = themeService.StatusText` in code-behind after each selection; do not add a binding proxy solely for this label.

- [ ] **Step 6: Wire theme and destination actions in code-behind**

Use this constructor shape:

```csharp
public ServantLibraryWindow(
    ServantLibraryViewModel viewModel,
    ThemeService themeService,
    MemoryWindow? memoryWindow = null,
    Action? showModelConnection = null)
```

Initialize choice state from `themeService.CurrentTheme`. In `OnThemeChoiceChecked`, parse the radio `Tag`, call `Select`, and update `ThemeStatusText` from `themeService.StatusText`. Keep `OnMemoryClick`. Add `OnModelConnectionClick` that invokes the supplied callback.

In `ServiceRegistration`, supply a callback that resolves, shows, and activates the singleton `ModelConnectionWindow`:

```csharp
() =>
{
    var window = provider.GetRequiredService<ModelConnectionWindow>();
    window.Show();
    window.Activate();
}
```

- [ ] **Step 7: Run focused tests and the WPF build**

Run the commands from Step 3, then:

```powershell
dotnet build src/FgoPet.App/FgoPet.App.csproj
```

Expected: all focused tests pass and XAML compiles.

- [ ] **Step 8: Commit the primary settings shell**

```powershell
git add src/FgoPet.App/Servants/ServantLibraryViewModel.cs src/FgoPet.App/Servants/ServantLibraryWindow.xaml src/FgoPet.App/Servants/ServantLibraryWindow.xaml.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs tests/FgoPet.Windows.Tests/Servants/ServantLibraryWindowIntegrationTests.cs
git commit -m "feat: rebuild settings as sidebar navigation"
```

---

### Task 4: Restyle and regroup the model connection window

**Files:**
- Modify: `src/FgoPet.App/Settings/ModelConnectionWindow.xaml`
- Modify: `tests/FgoPet.Windows.Tests/Settings/ModelConnectionWindowIntegrationTests.cs`

**Interfaces:**
- Consumes: shared brushes and keyed settings styles from Task 2.
- Preserves: window title `模型连接`, `ProviderComboBox`, `ApiKeyBox`, `ModelTextBox`, all existing commands, and `OfflineButton_Click`.
- Produces named section borders `CredentialSection`, `EndpointSection`, `ModelSection`, and footer `ConnectionActions` for integration verification.

- [ ] **Step 1: Write failing structure and resource tests**

Extend `ModelConnectionWindowIntegrationTests`:

```csharp
Assert.NotNull(window.CredentialSection);
Assert.NotNull(window.EndpointSection);
Assert.NotNull(window.ModelSection);
Assert.NotNull(window.ConnectionActions);
Assert.Equal(window.TryFindResource("SettingsWindowBackgroundBrush"), window.Background);
Assert.Same(viewModel.TestCommand, FindButton(window, "测试连接").Command);
Assert.Same(viewModel.SaveCommand, FindButton(window, "保存").Command);
```

Add this visual-tree helper locally to the test file; do not expose production fields solely for test traversal:

```csharp
private static Button FindButton(DependencyObject root, string content)
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is Button button && Equals(button.Content, content)) return button;
        try { return FindButton(child, content); }
        catch (InvalidOperationException) { }
    }

    throw new InvalidOperationException($"Button not found: {content}");
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter FullyQualifiedName~ModelConnectionWindowIntegrationTests
```

Expected: named sections are missing and the window does not consume shared resources.

- [ ] **Step 3: Rebuild the model window XAML**

Set the window background with `DynamicResource SettingsWindowBackgroundBrush`. Use a page header, a scrollable content stack, and three named card borders:

- `CredentialSection`: provider, key field, key-saved hint, and clear action.
- `EndpointSection`: Base URL and explanatory caption.
- `ModelSection`: current model field, refresh action, and available models list.

Use the shared keyed input and button styles. Preserve every existing binding and control name. Place status/error/offline copy above `ConnectionActions`; make Save primary, Test secondary, and Offline ghost/low emphasis.

- [ ] **Step 4: Run the focused test and build**

Run the command from Step 2, then:

```powershell
dotnet build src/FgoPet.App/FgoPet.App.csproj
```

Expected: the integration test passes and the XAML build succeeds.

- [ ] **Step 5: Commit the model-window redesign**

```powershell
git add src/FgoPet.App/Settings/ModelConnectionWindow.xaml tests/FgoPet.Windows.Tests/Settings/ModelConnectionWindowIntegrationTests.cs
git commit -m "feat: redesign model connection settings"
```

---

### Task 5: Verify both themes and the full regression suite

**Files:**
- Modify only if verification exposes a defect in files already named by Tasks 1–4.
- Record no generated screenshots or local application settings in Git.

**Interfaces:**
- Consumes the completed settings shell, theme service, and model window.
- Produces machine and visual evidence that the approved design is complete.

- [ ] **Step 1: Run all automated tests**

Run:

```powershell
dotnet test FgoPet.sln --no-restore
```

Expected: all test projects pass. If a Windows-only test is explicitly skipped by the project configuration, record the exact skip in the handoff rather than claiming it ran.

- [ ] **Step 2: Run a Release build**

Run:

```powershell
dotnet build FgoPet.sln -c Release --no-restore
```

Expected: build succeeds with zero errors.

- [ ] **Step 3: Perform the Modern Gray visual check**

Launch the app from the built project, open “从者库与设置”, and check all six destinations. Open the independent model window. Verify minimum sizes, keyboard focus, disabled states, warning/error colors, and that existing content is not clipped at 100% and the current Windows scale.

- [ ] **Step 4: Perform the FGO Light and live-switch check**

Open `Theme / 主题`, select FGO Light, and verify both already-open windows update without losing servant selection or text typed into `ModelTextBox`. Close and restart the app; verify FGO Light persists. Switch back to Modern Gray before the final handoff so new screenshots and normal development runs use the approved default.

- [ ] **Step 5: Inspect the final diff for scope and accidental files**

Run:

```powershell
git status --short
git diff --check
git diff --stat HEAD~4..HEAD
```

Expected: only planned source/test files and pre-existing user changes are present; no `.superpowers`, local settings, screenshots, build outputs, credentials, or database files are staged.

- [ ] **Step 6: Commit verification-only fixes if needed**

If Steps 1–5 required a source fix, stage only the repaired planned files and commit:

```powershell
git commit -m "fix: polish settings theme verification"
```

If no fix was needed, do not create an empty commit.
