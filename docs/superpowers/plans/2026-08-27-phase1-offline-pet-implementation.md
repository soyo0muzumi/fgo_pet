# Phase 1 Offline Pet and Servant Packs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a Windows 11 .NET 8 WPF desktop-pet host that starts offline with Mash, installs code-free servant packs, survives DPI/display changes, and exposes bounded collapsible dialogue/Todo UI.

**Architecture:** A modular monolith keeps domain contracts in `FgoPet.Core`, file/package/settings implementations in `FgoPet.Infrastructure`, and all WPF bitmap/window/UI code in `FgoPet.App`. Servant switching uses validated immutable snapshots and two-phase activation; `.fgopetpack` archives are data-only and installed transactionally.

**Tech Stack:** .NET 8, WPF, xUnit, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection/Configuration/Logging, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-08-27-phase1-offline-pet-and-servant-packs-design.md`

## Global Constraints

- Target Windows 11 and `net8.0-windows`; Core and Infrastructure remain WPF-free.
- Use WPF layered `Image` controls with `AllowsTransparency=True`; never add SkiaSharp or a renderer/DWM selector.
- Decode images with `BitmapCacheOption.OnLoad`, then `Freeze()`; no runtime file handles may remain open.
- Supported portrait scales are exactly `0.50`, `0.60`, and `0.75`; default is `0.50`.
- Every body, overlay, bottom-anchor, and panel-anchor edge comes from one source-pixel-to-device-pixel transform.
- Packs contain data only. Reject DLL, EXE, script, XAML, HTML, shader, link, absolute-path, traversal, and undeclared content.
- Phase 1 stores but never executes persona or prompt resources.
- Do not modify the disposable `spikes/rendering` project except when copying a test fixture verbatim is useful; production code lives under top-level `src/` and `tests/`.
- Do not add Todo persistence, LLM, Codex, event-center, pomodoro, GitHub API, online catalog, signing, startup registration, or installer work.

---

### Task 1: Scaffold the production solution and host

**Files:**
- Create: `FgoPet.sln`
- Create: `src/FgoPet.Core/FgoPet.Core.csproj`
- Create: `src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj`
- Create: `src/FgoPet.App/FgoPet.App.csproj`
- Create: `src/FgoPet.App/App.xaml`
- Create: `src/FgoPet.App/App.xaml.cs`
- Create: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Create: `tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj`
- Create: `tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj`
- Create: `tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj`
- Create: `tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj`
- Create: `tests/FgoPet.App.Tests/StaTest.cs`
- Create: `tests/FgoPet.App.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces: `ServiceRegistration.AddFgoPet(IServiceCollection, string[]) : IServiceCollection`.
- Produces: four test projects used by all later tasks.

- [ ] **Step 1: Create the solution/projects and failing architecture test**

```csharp
[Fact]
public void Production_projects_do_not_reference_SkiaSharp()
{
    var files = Directory.GetFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}spikes{Path.DirectorySeparatorChar}"));
    Assert.DoesNotContain(files, path => File.ReadAllText(path).Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase));
}
```

Add `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Configuration.Json`, and `Microsoft.Extensions.Logging` only to projects that consume them. Reference `Core` from Infrastructure and reference both from App.

- [ ] **Step 2: Run the architecture test and confirm it initially fails because the solution/projects are incomplete**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter ArchitectureTests -v normal`  
Expected: FAIL until project discovery and `RepoRoot()` are implemented.

- [ ] **Step 3: Add deterministic host composition**

```csharp
public static class ServiceRegistration
{
    public static IServiceCollection AddFgoPet(this IServiceCollection services, string[] args) => services
        .AddSingleton(TimeProvider.System)
        .AddLogging(builder => builder.AddDebug())
        .AddSingleton<AppStartup>();
}
```

`App.OnStartup` builds the provider, resolves `AppStartup`, and returns exit code 2 after showing a startup-error window if composition fails. Do not use a service locator from views.

- [ ] **Step 4: Build and test the empty host**

Run: `dotnet build FgoPet.sln -c Release -warnaserror`  
Expected: PASS, zero warnings.  
Run: `dotnet test FgoPet.sln -c Release --no-build`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add FgoPet.sln src/FgoPet.* tests/FgoPet.*
git commit -m "build: scaffold production WPF host"
```

### Task 2: Define pack/art contracts and expression semantics

**Files:**
- Create: `src/FgoPet.Core/Packs/PackContracts.cs`
- Create: `src/FgoPet.Core/Packs/PackError.cs`
- Create: `src/FgoPet.Core/Portraits/ExpressionSemantic.cs`
- Create: `src/FgoPet.Core/Portraits/ExpressionResolver.cs`
- Create: `tests/FgoPet.Core.Tests/Packs/PackContractTests.cs`
- Create: `tests/FgoPet.Core.Tests/Portraits/ExpressionResolverTests.cs`
- Create: `tests/fixtures/packs/mash-art-v2.json`
- Create: `tests/fixtures/packs/mash-art-v3.json`

**Interfaces:**
- Produces: `PackManifestV1`, `AppearanceManifestV3`, `ArtAssetV3`, `CompositionV3` immutable records.
- Produces: `enum ExpressionSemantic { Neutral, Happy, Excited, Shy, Concerned, Sad, Surprised, Angry }`.
- Produces: `ExpressionResolution Resolve(ExpressionSemantic requested, AppearanceManifestV3 manifest)`.
- Produces: `PackFailure(PackErrorCode Code, string Message, string? RelativePath)`.

- [ ] **Step 1: Write failing strict-contract and resolver tests**

```csharp
[Fact]
public void Resolve_follows_mapping_then_fallback_to_neutral()
{
    var manifest = Fixture.Appearance(mapping: new() { ["sad"] = "missing", ["neutral"] = "face01" },
                                      fallback: new() { ["sad"] = "neutral" });
    Assert.Equal(new ExpressionResolution(ExpressionSemantic.Sad, "face01", true),
                 new ExpressionResolver().Resolve(ExpressionSemantic.Sad, manifest));
}
```

Also assert all eight mappings exist, fallback cycles fail with `ExpressionMappingInvalid`, `neutral` resolves to a declared expression, default scale is one of the three allowed values, duplicate IDs fail, and unknown JSON properties fail deserialization.

- [ ] **Step 2: Run tests to verify missing types fail compilation**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "PackContractTests|ExpressionResolverTests"`  
Expected: FAIL with missing contract/resolver types.

- [ ] **Step 3: Implement exact immutable contracts and resolver**

```csharp
public sealed record ExpressionResolution(
    ExpressionSemantic Requested,
    string AssetId,
    bool UsedFallback);

public interface IExpressionResolver
{
    ExpressionResolution Resolve(ExpressionSemantic requested, AppearanceManifestV3 manifest);
}
```

Use ordinal stable-ID comparison and a visited set while walking fallbacks. Configure source-generated `System.Text.Json` metadata with unmapped-member rejection. Preserve v2 fixture meaning; v3 removes the fixed 7x4 requirement while preserving Mash IDs during conversion.

- [ ] **Step 4: Run contract tests**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "PackContractTests|ExpressionResolverTests"`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core tests/FgoPet.Core.Tests tests/fixtures/packs
git commit -m "feat: define servant pack and expression contracts"
```

### Task 3: Port unified portrait and attached-panel geometry

**Files:**
- Create: `src/FgoPet.Core/Geometry/GeometryTypes.cs`
- Create: `src/FgoPet.Core/Geometry/PortraitLayout.cs`
- Create: `src/FgoPet.Core/Geometry/AttachedPanelLayout.cs`
- Create: `src/FgoPet.Core/Windowing/ScreenLayout.cs`
- Create: `tests/FgoPet.Core.Tests/Geometry/PortraitLayoutTests.cs`
- Create: `tests/FgoPet.Core.Tests/Geometry/AttachedPanelLayoutTests.cs`
- Create: `tests/FgoPet.Core.Tests/Windowing/ScreenLayoutTests.cs`

**Interfaces:**
- Produces: `PortraitGeometry PortraitLayout.Calculate(PortraitSourceGeometry, double scale, Dpi2 dpi)`.
- Produces: `PanelPlacement AttachedPanelLayout.Place(DevicePoint anchor, DeviceSize desired, DeviceRect workArea, DeviceRect portrait)`.
- Produces: `DeviceRect ScreenLayout.Restore(SavedPlacement saved, IReadOnlyList<MonitorInfo> monitors, DeviceSize window)`.

- [ ] **Step 1: Port Phase 0 tests and add non-uniform DPI, negative-coordinate, flip, and clamp cases**

```csharp
[Theory]
[InlineData(1.5, 2.0)]
[InlineData(2.0, 1.5)]
public void Calculate_aligns_every_edge_with_one_transform(double x, double y)
{
    var result = PortraitLayout.Calculate(Fixture.MashGeometry, .5, new Dpi2(x, y));
    Assert.Equal(Round(13 * .5 * x), result.OverlayDeviceRect.X);
    Assert.Equal(Round(360 * .5 * y), result.PanelAnchorDevice.Y);
    Assert.Equal(result.BodyDeviceRect.Bottom, result.BottomAnchorDevice.Y);
}
```

- [ ] **Step 2: Verify tests fail before implementation**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "Geometry|ScreenLayout"`  
Expected: FAIL with missing geometry types.

- [ ] **Step 3: Implement WPF-free integer-device-pixel algorithms**

Round every source edge once to device pixels, then derive logical values. Panel placement prefers left, flips right when necessary, clamps vertically, and caps expanded height to 60% of the selected work area. Screen restoration matches monitor ID, then maximum overlap, then primary monitor, while keeping the portrait drag region visible.

- [ ] **Step 4: Run geometry tests**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "Geometry|ScreenLayout"`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Geometry src/FgoPet.Core/Windowing tests/FgoPet.Core.Tests
git commit -m "feat: add DPI-safe portrait and panel geometry"
```

### Task 4: Validate art v3 and load frozen WPF snapshots

**Files:**
- Create: `src/FgoPet.Infrastructure/Packs/AppearanceManifestReader.cs`
- Create: `src/FgoPet.Infrastructure/Packs/AppearanceValidator.cs`
- Create: `src/FgoPet.App/Portraits/BitmapAssetLoader.cs`
- Create: `src/FgoPet.App/Portraits/PortraitSnapshot.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Packs/AppearanceValidatorTests.cs`
- Create: `tests/FgoPet.App.Tests/Portraits/BitmapAssetLoaderTests.cs`

**Interfaces:**
- Produces: `AppearanceManifestV3 Read(string absoluteManifestPath)`.
- Produces: `ValidationResult Validate(AppearanceManifestV3 manifest, string root)`.
- Produces: `PortraitSnapshot LoadValidated(ValidatedAppearance appearance)` containing frozen images and source Alpha masks.

- [ ] **Step 1: Write failing validation and bitmap-lifecycle tests**

Cover absolute manifest entry, root confinement, missing file, hash mismatch, decode failure, invisible Alpha, size mismatch, overlay bounds, panel-anchor bounds, and released file handles. Assert every loaded `BitmapSource.IsFrozen` and overwrite each source PNG immediately after load.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter AppearanceValidatorTests`  
Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter BitmapAssetLoaderTests`  
Expected: FAIL with missing readers/loaders.

- [ ] **Step 3: Implement validation and OnLoad/frozen bitmap loading**

```csharp
using var stream = File.OpenRead(path);
var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
var frame = decoder.Frames[0];
frame.Freeze();
```

Convert once to BGRA32 to build immutable source Alpha masks. Return typed `PackFailure`; never silently substitute neutral for file/hash/decode errors.

- [ ] **Step 4: Run focused tests and full build**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter AppearanceValidatorTests`  
Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter BitmapAssetLoaderTests`  
Run: `dotnet build FgoPet.sln -c Release -warnaserror`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Infrastructure/Packs src/FgoPet.App/Portraits tests/FgoPet.*
git commit -m "feat: validate and load frozen portrait assets"
```

### Task 5: Build the stable WPF portrait view and offline Mash startup

**Files:**
- Create: `src/FgoPet.App/Portraits/PortraitView.xaml`
- Create: `src/FgoPet.App/Portraits/PortraitView.xaml.cs`
- Create: `src/FgoPet.App/Portraits/PortraitViewModel.cs`
- Create: `src/FgoPet.App/Main/PortraitWindow.xaml`
- Create: `src/FgoPet.App/Main/PortraitWindow.xaml.cs`
- Create: `src/FgoPet.App/Bootstrap/AppStartup.cs`
- Create: `src/FgoPet.App/Resources/Packs/official.mash/1.0.0/**`
- Create: `tests/FgoPet.App.Tests/Portraits/PortraitViewTests.cs`
- Create: `tests/FgoPet.App.Tests/Bootstrap/OfflineStartupTests.cs`

**Interfaces:**
- Produces: `PortraitView.Load(PortraitSnapshot, PortraitGeometry)` and `SetExpression(string assetId)`.
- Consumes: Task 3 geometry and Task 4 `PortraitSnapshot`.

- [ ] **Step 1: Write STA tests for stable body/canvas and offline startup**

```csharp
view.Load(snapshot, geometry);
var originalBody = view.BodySourceForTest;
var originalSize = view.RenderSize;
view.SetExpression("r01c02");
Assert.Same(originalBody, view.BodySourceForTest);
Assert.Equal(originalSize, view.RenderSize);
Assert.Same(snapshot.Images["r01c02"], view.ExpressionSourceForTest);
```

- [ ] **Step 2: Run tests and confirm failure**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "PortraitViewTests|OfflineStartupTests"`  
Expected: FAIL with missing view/startup.

- [ ] **Step 3: Implement the two-Image Canvas and embed the converted Mash pack**

Use `Stretch.Fill`, `SnapsToDevicePixels`, `UseLayoutRounding`, and `BitmapScalingMode.HighQuality`. Convert the real external bundle through the packaging fixture/process; do not commit raw Atlas source. If licensed runtime images are intentionally repository-external, make the build copy from the documented local generated-pack path and make the startup test use a generated fixture.

- [ ] **Step 4: Verify offline startup and rendering tests**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "PortraitViewTests|OfflineStartupTests"`  
Expected: PASS.  
Run: `dotnet run --project src/FgoPet.App/FgoPet.App.csproj -c Release -- --smoke-test`  
Expected: process reports `official.mash/casual`, creates the window, then exits 0 without Python/LLM/Codex.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.App tests/FgoPet.App.Tests
git commit -m "feat: start offline with layered Mash portrait"
```

### Task 6: Install `.fgopetpack` archives transactionally

**Files:**
- Create: `src/FgoPet.Core/Packs/IPackInstaller.cs`
- Create: `src/FgoPet.Infrastructure/Packs/PackArchivePolicy.cs`
- Create: `src/FgoPet.Infrastructure/Packs/FgoPetPackInstaller.cs`
- Create: `src/FgoPet.Infrastructure/FileSystem/IAtomicDirectoryMover.cs`
- Create: `src/FgoPet.Infrastructure/FileSystem/AtomicDirectoryMover.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Packs/FgoPetPackInstallerTests.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Packs/MaliciousArchiveFixtures.cs`

**Interfaces:**
- Produces: `Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken)`.
- Produces: `PackArchivePolicy(MaxEntries, MaxEntryBytes, MaxExpandedBytes, AllowedExtensions)` with fixed production defaults recorded in tests.

- [ ] **Step 1: Write failing happy-path and hostile-archive tests**

Test Zip Slip, absolute paths, links/reparse targets, DLL/EXE/PS1/XAML/HTML, duplicate normalized paths, entry-count excess, per-entry excess, total expanded excess, truncated archives, malformed manifests, existing version, cancellation, and cleanup after validation/move failure.

- [ ] **Step 2: Verify failures**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter FgoPetPackInstallerTests`  
Expected: FAIL with missing installer.

- [ ] **Step 3: Implement staging, validation, and atomic commit**

```csharp
public sealed record PackInstallResult(
    bool Installed,
    PackIdentity? Identity,
    PackFailure? Failure);
```

Resolve every destination under a random staging root, validate before extracting content, flush files, validate the extracted tree, then move to `%LocalAppData%/FgoPet/Packages/<package-id>/<version>`. Never overwrite an existing version.

- [ ] **Step 4: Run installer tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter FgoPetPackInstallerTests`  
Expected: PASS and every test temp directory empty after disposal.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Packs src/FgoPet.Infrastructure tests/FgoPet.Infrastructure.Tests
git commit -m "feat: install servant packs transactionally"
```

### Task 7: Discover versions, persist the index, and recover a valid pack

**Files:**
- Create: `src/FgoPet.Core/Packs/IArtPackageRepository.cs`
- Create: `src/FgoPet.Infrastructure/Packs/FileArtPackageRepository.cs`
- Create: `src/FgoPet.Infrastructure/Packs/PackIndexStore.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Packs/FileArtPackageRepositoryTests.cs`

**Interfaces:**
- Produces: `ScanAsync`, `ListServantsAsync`, `GetAppearanceAsync`, `RemoveAsync`, `MarkLastKnownGoodAsync`, and `ResolveStartupSelectionAsync`.
- Produces: recovery order current version -> prior valid same-package version -> last-known-good -> embedded Mash.

- [ ] **Step 1: Write repository/version/recovery tests**

Assert deterministic SemVer ordering, duplicate package identity rejection, rescan addition/removal, current-package uninstall refusal, embedded-package uninstall refusal, corrupted index quarantine, and all four recovery stages.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter FileArtPackageRepositoryTests`  
Expected: FAIL with missing repository.

- [ ] **Step 3: Implement repository with atomic versioned JSON index**

```csharp
public interface IArtPackageRepository
{
    Task<PackCatalog> ScanAsync(CancellationToken cancellationToken);
    Task<AppearanceLocation> ResolveStartupSelectionAsync(PortraitSelection requested, CancellationToken cancellationToken);
    Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken);
}
```

Quarantine malformed index files with a timestamp suffix, rebuild by scanning directories, and never persist absolute asset paths in user-visible diagnostics.

- [ ] **Step 4: Run repository tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter FileArtPackageRepositoryTests`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Packs src/FgoPet.Infrastructure/Packs tests/FgoPet.Infrastructure.Tests
git commit -m "feat: discover and recover servant pack versions"
```

### Task 8: Activate portraits with bounded snapshots

**Files:**
- Create: `src/FgoPet.Core/Portraits/IPortraitController.cs`
- Create: `src/FgoPet.App/Portraits/PortraitController.cs`
- Create: `src/FgoPet.App/Portraits/PortraitSnapshotCache.cs`
- Create: `tests/FgoPet.App.Tests/Portraits/PortraitControllerTests.cs`
- Create: `tests/FgoPet.App.Tests/Portraits/PortraitSnapshotCacheTests.cs`

**Interfaces:**
- Produces: `ActivateAsync(PortraitSelection, CancellationToken)`, `SetExpression(ExpressionSemantic)`, `SetScale(double)`.
- Publishes immutable `PortraitState` only after a complete snapshot succeeds.
- Cache capacity is exactly current plus one recent appearance.

- [ ] **Step 1: Write failing two-phase activation/cache tests**

Assert UI state is unchanged while loading, a failed candidate preserves old state, successful activation marks last-known-good, expression changes preserve body and geometry, invalid scale fails, and loading a third appearance evicts the oldest unreferenced snapshot.

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "PortraitControllerTests|PortraitSnapshotCacheTests"`  
Expected: FAIL with missing controller/cache.

- [ ] **Step 3: Implement immutable activation and UI-dispatch commit**

```csharp
public sealed record PortraitState(
    PortraitSelection Selection,
    ExpressionSemantic Semantic,
    string ExpressionAssetId,
    double Scale,
    PortraitSnapshot Snapshot,
    PortraitGeometry Geometry);
```

Load and validate off the UI thread; marshal only the final state replacement to the WPF dispatcher. Cancel superseded activations and never publish partial state.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "PortraitControllerTests|PortraitSnapshotCacheTests"`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Portraits src/FgoPet.App/Portraits tests/FgoPet.App.Tests
git commit -m "feat: activate portraits with bounded snapshots"
```

### Task 9: Implement settings, placement persistence, and monitor abstraction

**Files:**
- Create: `src/FgoPet.Core/Settings/AppSettings.cs`
- Create: `src/FgoPet.Core/Settings/IAppSettingsStore.cs`
- Create: `src/FgoPet.Core/Windowing/IWindowPlacementStore.cs`
- Create: `src/FgoPet.Core/Windowing/IScreenLayoutService.cs`
- Create: `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`
- Create: `src/FgoPet.Infrastructure/Windowing/JsonWindowPlacementStore.cs`
- Create: `src/FgoPet.Infrastructure/Windowing/WindowsScreenLayoutService.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Settings/JsonSettingsTests.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Windowing/WindowPlacementTests.cs`

**Interfaces:**
- Stores user settings separately from transient window placement under `%LocalAppData%/FgoPet`.
- Produces versioned DTO migration and atomic write behavior.

- [ ] **Step 1: Write failing default/migration/corruption/atomic-write tests**

Defaults are embedded Mash, casual appearance, scale .5, topmost true, auto-collapse true. Corrupt JSON is renamed for diagnosis and replaced in memory with defaults. A simulated write interruption preserves the previous valid file.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter "JsonSettingsTests|WindowPlacementTests"`  
Expected: FAIL.

- [ ] **Step 3: Implement versioned stores and Windows monitor adapter**

```csharp
public sealed record AppSettingsV1(
    PortraitSelection Selection,
    double Scale,
    bool Topmost,
    bool AutoCollapseExpandedPanel);
```

Save DIP coordinates relative to monitor work area plus monitor ID and saved DPI. Keep Win32 monitor enumeration behind `IScreenLayoutService` so restoration logic remains unit-testable.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter "JsonSettingsTests|WindowPlacementTests"`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Settings src/FgoPet.Core/Windowing src/FgoPet.Infrastructure tests/FgoPet.Infrastructure.Tests
git commit -m "feat: persist settings and visible window placement"
```

### Task 10: Complete window DPI, hit testing, gesture, tray, and single instance

**Files:**
- Create: `src/FgoPet.App/Windowing/AlphaHitTestService.cs`
- Create: `src/FgoPet.App/Windowing/PointerGestureRecognizer.cs`
- Create: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs`
- Create: `src/FgoPet.App/Lifetime/SingleInstanceCoordinator.cs`
- Create: `src/FgoPet.App/Lifetime/AppLifetimeService.cs`
- Create: `src/FgoPet.App/Tray/TrayService.cs`
- Modify: `src/FgoPet.App/Main/PortraitWindow.xaml.cs`
- Create: `tests/FgoPet.App.Tests/Windowing/AlphaHitTestServiceTests.cs`
- Create: `tests/FgoPet.App.Tests/Windowing/PointerGestureRecognizerTests.cs`
- Create: `tests/FgoPet.Windows.Tests/Lifetime/SingleInstanceTests.cs`
- Create: `tests/FgoPet.Windows.Tests/Windowing/PortraitWindowIntegrationTests.cs`

**Interfaces:**
- Produces: source-coordinate Alpha hit testing with no per-query image allocation.
- Produces: click versus drag using Windows system drag thresholds.
- Produces: second-instance activation and `.fgopetpack` path forwarding.

- [ ] **Step 1: Write unit and opt-in Windows integration tests**

Assert body/overlay union hits, transparent pixels return `HTTRANSPARENT`, panel rectangle remains interactive, DPI mapping is correct, threshold equality is click, threshold excess is drag, and right-click is ignored outside hit regions. Integration tests use category `WindowsIntegration` and a unique per-test instance key.

- [ ] **Step 2: Run unit tests to verify failure**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "AlphaHitTestServiceTests|PointerGestureRecognizerTests"`  
Expected: FAIL.

- [ ] **Step 3: Implement window messages, gestures, tray, and lifetime**

Handle `WM_NCHITTEST`, `WM_DPICHANGED`, display/work-area changes, close-to-exit only through `IAppLifetimeService`, and dispatcher-safe second-instance messages. Tray items are Show/Hide, Servant Library & Settings, Open Pack Folder, and Exit. `ShowInTaskbar=false`; tray always exists until normal exit.

- [ ] **Step 4: Run tests and manual smoke**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "AlphaHitTestServiceTests|PointerGestureRecognizerTests"`  
Expected: PASS.  
Run: `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter Category=WindowsIntegration`  
Expected: PASS on an interactive Windows desktop; otherwise document SKIP with environment reason.  
Manual: verify transparent desktop clicks pass through, portrait drag saves placement, tray restores a hidden window, and a second launch activates the first.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.App tests/FgoPet.App.Tests tests/FgoPet.Windows.Tests
git commit -m "feat: add resilient transparent window lifecycle"
```

### Task 11: Build the independent servant library and package management UI

**Files:**
- Create: `src/FgoPet.App/Servants/ServantLibraryWindow.xaml`
- Create: `src/FgoPet.App/Servants/ServantLibraryWindow.xaml.cs`
- Create: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Create: `src/FgoPet.App/Servants/ServantCardViewModel.cs`
- Create: `src/FgoPet.App/Servants/PackageDiagnosticViewModel.cs`
- Create: `tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs`

**Interfaces:**
- Consumes: `IArtPackageRepository`, `IPackInstaller`, `IPortraitController`, `IAppSettingsStore`.
- Produces commands: install, rescan, select servant, select appearance, uninstall third-party, open pack folder.

- [ ] **Step 1: Write failing ViewModel workflow tests**

Test catalog grouping by servant, source badges, appearance selection, install without automatic activation, successful activation then settings save, failed activation preserving selection, embedded uninstall disabled, current-package uninstall blocked, and diagnostic error-code display without absolute paths.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter ServantLibraryViewModelTests`  
Expected: FAIL.

- [ ] **Step 3: Implement MVVM window and tabs**

Use left search/list, right preview/details, and navigation for Servant Library, General, Appearance & Window, and Diagnostics. File picker accepts `.fgopetpack`; all async commands disable conflicting operations and expose cancellation-safe progress.

- [ ] **Step 4: Run tests and keyboard/accessibility smoke**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter ServantLibraryViewModelTests`  
Expected: PASS.  
Manual: complete install, appearance selection, scale/topmost change, diagnostic reading, and close using keyboard only at 150% and 200% DPI.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.App/Servants tests/FgoPet.App.Tests/Servants
git commit -m "feat: add servant library and pack management"
```

### Task 12: Implement collapsible attached UI and recent-servant switcher

**Files:**
- Create: `src/FgoPet.Core/Panels/AttachedPanelState.cs`
- Create: `src/FgoPet.Core/Panels/AttachedPanelStateMachine.cs`
- Create: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Create: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs`
- Create: `src/FgoPet.App/Panels/DialogueItemViewModel.cs`
- Create: `src/FgoPet.App/Panels/TodoItemViewModel.cs`
- Create: `src/FgoPet.App/Panels/RecentServantSwitcherViewModel.cs`
- Create: `src/FgoPet.App/Panels/PanelFixtures.cs`
- Create: `tests/FgoPet.Core.Tests/Panels/AttachedPanelStateMachineTests.cs`
- Create: `tests/FgoPet.App.Tests/Panels/AttachedPanelViewModelTests.cs`
- Create: `tests/FgoPet.App.Tests/Panels/AttachedPanelLayoutIntegrationTests.cs`

**Interfaces:**
- Produces states `Collapsed`, `Compact`, `ExpandedDialogue`, `ExpandedTodo`.
- Dialogue retains at most 20 items and presents about 6; Todo presents 8 rows and scrolls overflow.
- Recent switcher contains 3-5 entries and delegates activation to `IPortraitController`.

- [ ] **Step 1: Write failing state, bound, timeout, and layout tests**

```csharp
[Theory]
[InlineData(AttachedPanelState.Collapsed, PanelAction.PortraitClick, AttachedPanelState.Compact)]
[InlineData(AttachedPanelState.Compact, PanelAction.DialogueClick, AttachedPanelState.ExpandedDialogue)]
[InlineData(AttachedPanelState.ExpandedDialogue, PanelAction.DialogueClick, AttachedPanelState.Compact)]
[InlineData(AttachedPanelState.ExpandedTodo, PanelAction.Escape, AttachedPanelState.Compact)]
public void Transition_is_deterministic(AttachedPanelState from, PanelAction action, AttachedPanelState expected) =>
    Assert.Equal(expected, AttachedPanelStateMachine.Transition(from, action));
```

Also test 30-second idle collapse only when pointer is outside, disabled auto-collapse, startup always Collapsed, dialogue eviction at 21, Todo scrolling after 8, left/right flip, 60% work-area cap, long Chinese, unbroken English, empty lists, large fonts, and no portrait-anchor movement.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter AttachedPanelStateMachineTests`  
Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "AttachedPanelViewModelTests|AttachedPanelLayoutIntegrationTests"`  
Expected: FAIL.

- [ ] **Step 3: Implement the state machine, data templates, timer, and quick switcher**

Use `TimeProvider` for deterministic idle tests. Collapsed removes the panel from measurement/hit regions; Compact shows dialogue, Todo, current-servant avatar, settings, and collapse controls; Expanded swaps only the content region. Opening Settings launches the independent Task 11 window.

- [ ] **Step 4: Run panel tests and manual interaction smoke**

Run: `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter AttachedPanelStateMachineTests`  
Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "AttachedPanelViewModelTests|AttachedPanelLayoutIntegrationTests"`  
Expected: PASS.  
Manual: verify click -> Compact -> Dialogue/Todo, Escape step-down, 30-second collapse, settings launch, 3-5 recent-servant switch, workspace flip, and no empty window interception after collapse.

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.Core/Panels src/FgoPet.App/Panels tests/FgoPet.*
git commit -m "feat: add bounded collapsible attached panels"
```

### Task 13: Harden diagnostics, soak tests, and release gates

**Files:**
- Create: `src/FgoPet.Core/Diagnostics/IResourceDiagnostics.cs`
- Create: `src/FgoPet.Infrastructure/Diagnostics/ResourceDiagnostics.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Diagnostics/ResourceDiagnosticsTests.cs`
- Create: `tests/FgoPet.Windows.Tests/Soak/PortraitSoakTests.cs`
- Create: `scripts/test-phase1.ps1`
- Create: `docs/testing/phase1-windows-matrix.md`
- Modify: `README.md`

**Interfaces:**
- Diagnostics accepts IDs, versions, error codes, relative paths, and exceptions; it rejects/redacts prompt/chat/credential/absolute external-source content.
- Test script runs Release build, all unit/STA tests, optional Windows integration, and records the manual matrix location.

- [ ] **Step 1: Write failing redaction and bounded-soak tests**

Assert log output omits prompt text, chat fixture text, API-key-like values, and raw source paths. Soak cycles 28 Mash expressions, three appearances to exercise eviction, and 1,000 panel open/close transitions; assert final controller/cache invariants and record working-set samples without imposing a flaky fixed memory ceiling.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter ResourceDiagnosticsTests`  
Run: `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter PortraitSoakTests`  
Expected: FAIL before diagnostics/soak harness exists.

- [ ] **Step 3: Implement diagnostics, test script, and manual matrix**

The matrix has explicit PASS/FAIL/evidence cells for Windows 11 at 200% and 150%, mixed-DPI dual monitors, negative coordinates, cross-screen drag, monitor disconnect, tray restore, transparent hit-through, all three scales, expression seams, pack failure recovery, and expanded-panel bounds.

- [ ] **Step 4: Run the complete verification gate**

Run: `pwsh -File scripts/test-phase1.ps1`  
Expected: Release build zero warnings/errors; all non-environmental tests PASS; Windows integration either PASS or explicitly SKIP only when no interactive desktop exists.  
Run: `dotnet list FgoPet.sln package | Select-String SkiaSharp`  
Expected: no output.  
Run: `rg -n "SkiaSharp|RenderBackend|TransparencyMode" src tests`  
Expected: no production references.

- [ ] **Step 5: Complete real-device evidence before release**

Fill `docs/testing/phase1-windows-matrix.md` with screenshots/log references for 200%, 150%, and mixed-DPI dual-monitor tests. Do not mark Phase 1 releasable while a required cell is unobserved.

- [ ] **Step 6: Commit**

```bash
git add src/FgoPet.Core/Diagnostics src/FgoPet.Infrastructure/Diagnostics tests scripts docs/testing README.md
git commit -m "test: add phase 1 diagnostics and release gates"
```

## Knowledge Map

| Step | Knowledge Source | Confidence |
|---|---|---|
| WPF image loading and layered portrait | Codebase: Phase 0 `ArtBundleLoader`, `PortraitLayout`, `WpfPortraitSurface` and tests | High |
| Rendering/DPI decisions | Accepted ADR `docs/decisions/0001-windows-portrait-renderer.md` | High |
| Pack/product boundaries and UI states | Approved Phase 1 spec and user decisions in planning | High |
| ZIP confinement, hashing, atomic staging | .NET standard-library behavior and security patterns; verified by hostile fixtures | High |
| Window messages, tray, multi-monitor behavior | WPF/Win32 platform APIs plus required real-device matrix | Medium |
| Real Mash runtime-art inclusion/legal distribution | Repository-external generated assets and project owner release policy | Medium; implementation must not commit raw source |
| GitHub Release upload automation | Outside the main plan; P1.4 produces artifacts, manual upload is sufficient for Phase 1 | High |

## Open Questions

- [ ] Decide the exact production archive limits (`MaxEntries`, per-file bytes, expanded bytes) during Task 6 by measuring the converted Mash pack, then lock conservative multiples in tests. This does not block Tasks 1-5.
- [ ] Confirm whether distributable Mash runtime PNGs may be committed or must be injected during packaging. This blocks the final Task 5 release asset, but not its generated test fixture or application code.
- [ ] Choose the concrete tray implementation compatible with .NET 8 WPF (`System.Windows.Forms.NotifyIcon` adapter or an approved lightweight package) during Task 10. Prefer the built-in adapter unless accessibility testing exposes a blocker.

## Implementation Checklist

- [ ] Task 1: Production solution and host
- [ ] Task 2: Pack/art contracts and expression semantics
- [ ] Task 3: Portrait, panel, and screen geometry
- [ ] Task 4: Art validation and frozen WPF snapshots
- [ ] Task 5: Stable portrait view and offline Mash startup
- [ ] Task 6: Transactional `.fgopetpack` installer
- [ ] Task 7: Repository, versions, and recovery
- [ ] Task 8: Two-phase portrait activation and bounded cache
- [ ] Task 9: Settings and placement persistence
- [ ] Task 10: Window lifecycle, hit testing, tray, and single instance
- [ ] Task 11: Independent servant library UI
- [ ] Task 12: Collapsible attached UI and recent switcher
- [ ] Task 13: Diagnostics, soak, and release gates

