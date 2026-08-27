# FGO Pet Phase 0 Rendering Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair Mash's runtime art, define a layered portrait contract, and use a disposable Windows 11 WPF probe to select the production portrait renderer and transparent-window composition mode.

**Architecture:** The existing Python content pipeline first preserves valid source alpha and emits an explicit body-plus-expression composition contract. An isolated .NET 8 WPF probe then loads that bundle, renders the same layered portrait through WPF and SkiaSharp, applies one shared scale/DPI transform, and compares conventional WPF transparency with a DWM composition experiment. The selected rules, not the spike code, become Phase 1 inputs.

**Tech Stack:** Python 3.11+, Pillow, Pydantic 2, pytest, C# 12, .NET SDK 8.0.121, .NET 8 WPF, xUnit, SkiaSharp 3.116.1, Windows 11 Per-Monitor DPI Awareness V2.

**Spec:** `docs/superpowers/specs/2026-08-27-phase0-rendering-validation-design.md`

**Supersedes:** `docs/superpowers/plans/2026-08-25-fgo-pet-rendering-spike.md`

## Global Constraints

- Target Windows 11 only.
- The probe is disposable and lives under `spikes/rendering`; Phase 1 must reimplement validated contracts instead of copying spike code wholesale.
- FGO art under `D:\fgo_unpack\fgo_assets` is read-only except for the explicit content-pipeline regeneration command in Task 1.
- Final renderer decisions must use a regenerated bundle that passes alpha QA and visual review; raw art may only support probe development before that gate.
- The portrait is a stable `full_body` base plus one `r01c01`–`r07c04` upper-body overlay.
- Body, overlay offset, portrait anchor, and panel anchor share one uniform scale: 50%, 60% default, or 75%.
- Compare Windows display scales 100%, 125%, and 150%; mixed-monitor movement remains `not-observed` and does not block Phase 0.
- Prefer WPF unless SkiaSharp is visibly better in at least two observed cells without exceeding WPF working set by more than approximately 30%.
- Prefer conventional `AllowsTransparency=True` unless DWM preserves correct behavior and provides a clear measured benefit.
- Use `D:\environments\anaconda\python.exe` for Python commands.
- Do not use `dotnet --info`; use `dotnet --version`, `dotnet --list-sdks`, and `dotnet --list-runtimes`.
- Every task ends with focused verification and a Git commit. Do not add `.learnings/`, generated art, captures, or the untracked daily report to a commit.

## File Map

### Content pipeline

- `src/fgo_pet_content/art/background.py` — preserve already-transparent crops and remove background only from fully opaque inputs.
- `src/fgo_pet_content/art/models.py` — schema v2 composition contract.
- `src/fgo_pet_content/art/export.py` — emit alpha-safe runtime images and composition metadata.
- `src/fgo_pet_content/art/qa.py` — reject runtime alpha loss and render a composite contact sheet.
- `tests/art/test_background_export.py` — alpha preservation and export regression tests.
- `tests/art/test_models.py` — composition schema validation.
- `tests/art/test_qa_cli.py` — QA rejection and CLI integration.
- `docs/reports/2026-08-27-mash-art-alpha-correction.md` — regenerated hashes and visual decision.

### Rendering probe

- `spikes/rendering/FgoPet.RenderingProbe.sln` — isolated solution.
- `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj` — WPF executable.
- `spikes/rendering/src/FgoPet.RenderingProbe/ProbeOptions.cs` — CLI contract.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/RenderBackend.cs` — renderer CLI values.
- `spikes/rendering/src/FgoPet.RenderingProbe/Art/ArtBundle.cs` — immutable bundle records.
- `spikes/rendering/src/FgoPet.RenderingProbe/Art/ArtBundleLoader.cs` — manifest and file validation.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/PortraitLayout.cs` — shared source/logical/device geometry.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/IRenderSurface.cs` — common layered-renderer contract.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/WpfPortraitSurface.cs` — native WPF backend.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/SkiaPortraitSurface.cs` — SkiaSharp backend.
- `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/TransparencyMode.cs` — process-level transparency selection.
- `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/LayeredWindowStyle.cs` — minimal DWM experiment hook.
- `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeSample.cs` — diagnostic schema.
- `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeRecorder.cs` — JSONL writer.
- `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml` — portrait, original Chaldea-terminal panel, and diagnostic overlay.
- `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml.cs` — controls, DPI handling, capture, and stress run.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/*` — unit and STA rendering tests.
- `spikes/rendering/README.md` — exact manual matrix.
- `docs/decisions/0001-windows-portrait-renderer.md` — binding renderer/composition decision.

---

### Task 1: Preserve existing alpha and restore Mash art readiness

**Files:**
- Modify: `src/fgo_pet_content/art/background.py`
- Modify: `src/fgo_pet_content/art/export.py`
- Modify: `src/fgo_pet_content/art/qa.py`
- Modify: `tests/art/test_background_export.py`
- Modify: `tests/art/test_qa_cli.py`
- Create: `docs/reports/2026-08-27-mash-art-alpha-correction.md`

**Interfaces:**
- Produces: `has_meaningful_transparency(image: Image.Image) -> bool`
- Preserves: `remove_edge_background(image, tolerance=32, feather=2) -> Image.Image`
- Produces: QA error `asset.runtime_alpha_loss`

- [ ] **Step 1: Add failing alpha-preservation tests**

Add a fixture with transparent corners, semi-transparent antialiased hair, an opaque dark garment touching the visible foreground, and transparent pixels whose hidden RGB resembles the old background. Assert:

```python
def test_existing_alpha_is_preserved_exactly() -> None:
    raw = transparent_character_fixture()
    runtime = remove_edge_background(raw, tolerance=32, feather=2)
    assert runtime.tobytes() == raw.convert("RGBA").tobytes()


def test_qa_rejects_runtime_alpha_below_raw(tmp_path: Path) -> None:
    bundle, manifest = valid_bundle(tmp_path)
    path = bundle / manifest.assets[0].runtime_path
    with Image.open(path) as opened:
        damaged = opened.convert("RGBA")
    # valid_bundle guarantees that (6, 6) is opaque in both raw and runtime.
    damaged.putpixel((6, 6), (*damaged.getpixel((6, 6))[:3], 0))
    damaged.save(path)
    report = validate_art_bundle(bundle)
    assert any(item.check_id == "asset.runtime_alpha_loss" for item in report.errors)
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
& 'D:\environments\anaconda\python.exe' -m pytest tests/art/test_background_export.py tests/art/test_qa_cli.py -q
```

Expected: the exact-preservation assertion fails and the QA report does not yet contain `asset.runtime_alpha_loss`.

- [ ] **Step 3: Implement alpha-aware export behavior**

Treat any crop with at least one `alpha < 255` pixel and at least one `alpha > 0` pixel as already transparent:

```python
def has_meaningful_transparency(image: Image.Image) -> bool:
    alpha = image.convert("RGBA").getchannel("A")
    minimum, maximum = alpha.getextrema()
    return minimum < 255 and maximum > 0


def remove_edge_background(image: Image.Image, *, tolerance: int = 32, feather: int = 2) -> Image.Image:
    rgba = image.convert("RGBA")
    if has_meaningful_transparency(rgba):
        return rgba.copy()
    # Existing opaque-background flood fill continues below.
```

In `validate_art_bundle`, open raw and runtime as RGBA, require equal dimensions, and append `asset.runtime_alpha_loss` when any runtime alpha byte is lower than its corresponding raw alpha byte.

- [ ] **Step 4: Run focused and full Python tests**

Run:

```powershell
& 'D:\environments\anaconda\python.exe' -m pytest tests/art -q
& 'D:\environments\anaconda\python.exe' -m pytest -q
```

Expected: all tests pass.

- [ ] **Step 5: Regenerate the external Mash bundle**

Run only after tests pass:

```powershell
& 'D:\environments\anaconda\python.exe' -m fgo_pet_content.cli art process-mash-casual `
  --source 'D:\fgo_unpack\fgo_assets\servant\000001\98001000_merged.png' `
  --output 'D:\fgo_unpack\fgo_assets\pet\mash\casual' `
  --labels 'content\servants\mash\casual-expression-labels.json'
& 'D:\environments\anaconda\python.exe' -m fgo_pet_content.cli art validate `
  --bundle 'D:\fgo_unpack\fgo_assets\pet\mash\casual'
```

Expected: 29 raw and 29 runtime PNGs; QA status `PASS`; `runtime/full_body.png` has no alpha values below `raw/full_body.png`.

- [ ] **Step 6: Visually review and record the correction**

Inspect the regenerated full body, four representative expressions, and contact sheet on a light checkerboard. Record before/after pixel counts, manifest hash, QA result, and `approved` or `rejected` in `docs/reports/2026-08-27-mash-art-alpha-correction.md`. Do not claim readiness when the visual result is rejected.

- [ ] **Step 7: Commit the alpha correction**

```powershell
git add src/fgo_pet_content/art tests/art docs/reports/2026-08-27-mash-art-alpha-correction.md
git commit -m "fix: preserve transparent Mash art"
```

---

### Task 2: Add the layered portrait composition contract

**Files:**
- Modify: `src/fgo_pet_content/art/models.py`
- Modify: `src/fgo_pet_content/art/export.py`
- Modify: `src/fgo_pet_content/art/qa.py`
- Modify: `tests/art/test_models.py`
- Modify: `tests/art/test_qa_cli.py`

**Interfaces:**
- Produces: `Point(x: int, y: int)`
- Produces: `Composition(body_id: str, default_expression_id: str, overlay_offset: Point, overlay_size: Size, panel_anchor: Point, default_scale: float)`
- Updates: `ArtManifest.schema_version == 2`
- Produces: `ArtManifest.composition: Composition`

- [ ] **Step 1: Write failing schema tests**

Assert schema v2 requires `body_id="full_body"`, an existing expression default, overlay size 256×240, non-negative in-bounds offset, a panel anchor inside the 303×603 body canvas, and `default_scale == 0.50`.

```python
composition = Composition(
    body_id="full_body",
    default_expression_id="r01c01",
    overlay_offset=Point(x=13, y=0),
    overlay_size=Size(width=256, height=240),
    panel_anchor=Point(x=151, y=360),
    default_scale=0.50,
)
manifest = ArtManifest(schema_version=2, source=SOURCE, assets=_complete_assets(), composition=composition)
assert manifest.composition.overlay_offset.x == 13
```

Also assert unknown default IDs, offsets that exceed the body canvas, and scales outside `(0, 1]` raise `ValidationError`.

- [ ] **Step 2: Run model tests and verify missing-type failures**

```powershell
& 'D:\environments\anaconda\python.exe' -m pytest tests/art/test_models.py -q
```

Expected: import or validation failures because composition types do not exist.

- [ ] **Step 3: Implement schema v2 and deterministic defaults**

Add frozen `Point`, `Size`, and `Composition` models. In `ArtManifest.validate_complete_bundle`, resolve the body and default expression assets and validate:

```python
if composition.body_id != "full_body":
    raise ValueError("composition body must be full_body")
if composition.default_expression_id not in set(ids) - {"full_body"}:
    raise ValueError("default expression must reference an expression asset")
if composition.overlay_offset.x + composition.overlay_size.width > body.crop_rect.width:
    raise ValueError("expression overlay exceeds body width")
if composition.overlay_offset.y + composition.overlay_size.height > body.crop_rect.height:
    raise ValueError("expression overlay exceeds body height")
```

Export the pixel-verified geometry `(13, 0)`, overlay size from `r01c01`, panel anchor `(151, 360)`, and final reviewed default scale `0.50`.

- [ ] **Step 4: Add composite QA**

Extend the contact sheet with four composite previews: `r01c01`, `r02c02`, `r04c04`, and `r07c03`. Composite runtime body and overlay using `overlay_offset`; label every preview with stable ID and offset. Reject overlay files whose pixel dimensions differ from `composition.overlay_size`.

- [ ] **Step 5: Run art tests and regenerate once more**

```powershell
& 'D:\environments\anaconda\python.exe' -m pytest tests/art -q
& 'D:\environments\anaconda\python.exe' -m fgo_pet_content.cli art process-mash-casual `
  --source 'D:\fgo_unpack\fgo_assets\servant\000001\98001000_merged.png' `
  --output 'D:\fgo_unpack\fgo_assets\pet\mash\casual' `
  --labels 'content\servants\mash\casual-expression-labels.json'
```

Expected: tests pass and the external manifest is schema v2 with explicit composition data.

- [ ] **Step 6: Visually approve or adjust one shared offset**

Inspect all 28 composites, focusing on hair, glasses, collar, shoulders, and jacket seams. The shared `(13, 0)` offset must remain seamless at the final 60% window scale. Do not introduce per-expression offsets unless the source images prove they differ; if they do, stop and amend the design before changing the schema.

- [ ] **Step 7: Commit the composition contract**

```powershell
git add src/fgo_pet_content/art tests/art
git commit -m "feat: define layered portrait composition"
```

---

### Task 3: Scaffold the isolated bundle-driven WPF probe

**Files:**
- Create: `spikes/rendering/FgoPet.RenderingProbe.sln`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/ProbeOptions.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/RenderBackend.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/TransparencyMode.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Art/ArtBundle.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Art/ArtBundleLoader.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeOptionsTests.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ArtBundleLoaderTests.cs`

**Interfaces:**
- Produces: `ProbeOptions.Parse(string[] args) -> ProbeOptions`
- Produces: `ProbeOptions(string BundlePath, RenderBackend Backend, TransparencyMode Transparency, double Scale, string OutputDirectory)`
- Produces: `ArtBundleLoader.Load(string manifestPath) -> ArtBundle`
- Produces: CLI `--bundle`, `--renderer wpf|skia`, `--transparency conventional|dwm`, `--scale 0.5|0.6|0.75`, `--output`

- [ ] **Step 1: Verify local prerequisites**

```powershell
git rev-parse --show-toplevel
dotnet --version
dotnet --list-sdks
dotnet --list-runtimes
```

Expected: the story-pipeline worktree, SDK 8.0.121, and a Windows Desktop 8 runtime. Stop on a different repository root.

- [ ] **Step 2: Write failing parser and bundle-loader tests**

Cover required absolute manifest path, enum parsing, only the three supported scales, output normalization, schema version 2, missing files, invalid hashes, invalid alpha, and missing composition IDs. Generate PNG fixtures in the test directory; never commit FGO art.

```csharp
[Theory]
[InlineData("0.5")]
[InlineData("0.6")]
[InlineData("0.75")]
public void Parse_accepts_supported_scales(string value) { /* assert parsed value */ }

[Fact]
public void Load_rejects_manifest_without_composition() { /* schema v1 fixture must throw */ }
```

- [ ] **Step 3: Run tests and verify compilation failure**

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj
```

Expected: missing `ProbeOptions`, `ArtBundle`, and loader types.

- [ ] **Step 4: Implement the minimal immutable contracts**

Use `System.Text.Json` records with camelCase property mappings. Load images with `BitmapCacheOption.OnLoad` only after validating the manifest and SHA-256. Throw `InvalidDataException` naming the exact stable ID and failed check. `App.OnStartup` catches startup errors, shows one message, sets exit code 2, and does not open a window.

Declare the CLI enums in this task so `ProbeOptions` is self-contained:

```csharp
public enum RenderBackend { Wpf, Skia }
public enum TransparencyMode { Conventional, Dwm }
```

- [ ] **Step 5: Run the complete scaffold tests**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release
```

Expected: all parser and loader tests pass.

- [ ] **Step 6: Commit the probe scaffold**

```powershell
git add spikes/rendering
git commit -m "spike: scaffold layered rendering probe"
```

---

### Task 4: Implement shared layout and the WPF layered renderer

**Files:**
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/PortraitLayout.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/IRenderSurface.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/WpfPortraitSurface.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/PortraitLayoutTests.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/WpfPortraitSurfaceTests.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/StaTest.cs`

**Interfaces:**
- Produces: `PortraitLayout.Calculate(ArtBundle bundle, double scale, DpiScale dpi) -> PortraitGeometry`
- Produces: `PortraitGeometry(LogicalSize, DeviceSize, OverlayLogicalRect, OverlayDeviceRect, BottomAnchor, PanelAnchor)`
- Produces: `IRenderSurface.Load(ArtBundle bundle)`, `SetExpression(string id)`, `ApplyGeometry(PortraitGeometry geometry)`, `Capture(DpiScale dpi)`

- [ ] **Step 1: Write failing geometry tests**

For a 303×603 body, 256×240 overlay at `(13, 0)`, assert 60% logical body size is 181.8×361.8 DIP before device alignment. At DPI 1.25 and 1.5, assert body edges, overlay edges, bottom anchor, and panel anchor derive from one shared transform and land on integer device pixels.

- [ ] **Step 2: Write failing WPF STA tests**

Generate synthetic body and overlay PNGs with distinct alpha/color regions. Assert the WPF surface:

- uses two `Image` children in one fixed coordinate space;
- preserves the same outer size when expression changes;
- places the overlay at the geometry rectangle;
- captures alpha and both layers;
- freezes decoded bitmaps and closes source files.

- [ ] **Step 3: Run focused tests and verify missing implementations**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release --filter "PortraitLayoutTests|WpfPortraitSurfaceTests"
```

- [ ] **Step 4: Implement one-transform layout**

Compute every logical coordinate from source pixels times the selected uniform scale, then align rectangle edges in device space:

```csharp
static double Align(double logical, double dpi) => Math.Round(logical * dpi) / dpi;
```

Derive overlay and anchors from the aligned body origin; never round body and overlay using independent scale values.

- [ ] **Step 5: Implement the WPF two-layer surface**

Use a `Canvas` with explicit logical width/height. Set `BitmapScalingMode.HighQuality`, `SnapsToDevicePixels=true`, and `UseLayoutRounding=true`. Replace only the overlay image source in `SetExpression`; do not rebuild the body or parent visual.

- [ ] **Step 6: Verify WPF rendering tests**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release --filter "PortraitLayoutTests|WpfPortraitSurfaceTests"
```

Expected: geometry, alpha, expression stability, and file-release tests pass.

- [ ] **Step 7: Commit shared geometry and WPF rendering**

```powershell
git add spikes/rendering
git commit -m "spike: render layered portrait with WPF"
```

---

### Task 5: Add the SkiaSharp comparison backend

**Files:**
- Modify: `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/SkiaPortraitSurface.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/SkiaPortraitSurfaceTests.cs`

**Interfaces:**
- Consumes: `IRenderSurface`, `ArtBundle`, and `PortraitGeometry`
- Produces: `SkiaPortraitSurface : IRenderSurface, IDisposable`

- [ ] **Step 1: Pin Skia packages and write failing parity tests**

Add exact package versions:

```xml
<PackageReference Include="SkiaSharp" Version="3.116.1" />
<PackageReference Include="SkiaSharp.Views.WPF" Version="3.116.1" />
```

Run the same synthetic bundle and geometry cases as WPF. Assert equal output dimensions, non-zero alpha at body and overlay sample points, stable outer size after expression change, and safe reload/dispose.

- [ ] **Step 2: Run focused tests and verify missing implementation**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release --filter SkiaPortraitSurfaceTests
```

- [ ] **Step 3: Implement explicit two-pass Skia drawing**

Load immutable `SKImage` objects, clear to transparent, draw the body into the geometry body rectangle, then draw the current expression into the overlay rectangle using `SKSamplingOptions(SKCubicResampler.Mitchell)`. Dispose replaced and final native resources deterministically.

- [ ] **Step 4: Run backend parity tests**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release --filter "WpfPortraitSurfaceTests|SkiaPortraitSurfaceTests"
```

Expected: both backends pass the same dimension, alpha, layer-order, switch, and disposal assertions.

- [ ] **Step 5: Commit the Skia backend**

```powershell
git add spikes/rendering
git commit -m "spike: add Skia layered portrait backend"
```

---

### Task 6: Build the interactive transparent probe and original panel

**Files:**
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/app.manifest`
- Modify: `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/TransparencyMode.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/LayeredWindowStyle.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeSample.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeRecorder.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeRecorderTests.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/StressSequenceTests.cs`

**Interfaces:**
- Produces: keyboard controls `F1`, `F2`, arrows, `1`–`3`, `P`, `T`, `C`, `R`, `Esc`
- Produces: `<output>/samples.jsonl`, `<output>/captures/*.png`, `<output>/session-summary.json`

- [ ] **Step 1: Write diagnostics and stress-sequence tests**

Assert JSONL uses camelCase, appends without retaining an open handle, and contains no `apiKey`, `prompt`, or `chatText`. Assert the stress sequence contains exactly 280 entries in stable row-major expression order and does not include `full_body` as an expression ID.

- [ ] **Step 2: Declare Per-Monitor V2 awareness**

Add `app.manifest` and set `ApplicationManifest` in the project. Include both `true/pm` and `PerMonitorV2` Windows settings.

- [ ] **Step 3: Implement the two process-level transparency modes**

Conventional mode uses `AllowsTransparency=True`, `WindowStyle=None`, and `Background=Transparent`.

DWM mode uses `AllowsTransparency=False`, `WindowStyle=None`, `Background=null`, and `WindowChrome.GlassFrameThickness=-1`. Install only the required HWND hook and remove it on close. Transparency mode cannot change after HWND creation.

- [ ] **Step 4: Implement the original Chaldea-terminal panel**

Create a dark navy semi-transparent panel with clipped corners, cyan/magenta accent lines, `Segoe UI` plus `Microsoft YaHei UI` fallback, and no copied FGO texture. Anchor it at the manifest panel point and draw it above the portrait so it covers the lower body. `P` toggles visibility; `T` switches static dialogue and Todo samples.

- [ ] **Step 5: Wire controls, capture, and DPI changes**

On `WM_DPICHANGED`, schedule one `DispatcherPriority.Loaded` callback that recalculates `PortraitGeometry`, applies it to the active backend, repositions the panel, refreshes diagnostics, and records a sample. On capture, await `DispatcherPriority.Render`, then save the portrait and panel at physical pixel dimensions.

- [ ] **Step 6: Implement the stress run**

`R` switches all 28 expressions ten times without rebuilding the window or body image. Record per-switch elapsed time and working set. After the run, perform one controlled GC measurement and write minimum, maximum, final, and post-GC working set to `session-summary.json`; do not use GC as part of normal switching.

- [ ] **Step 7: Run automated verification**

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release
dotnet build spikes/rendering/FgoPet.RenderingProbe.sln -c Release --no-restore
```

Expected: all tests pass and build completes without new warnings.

- [ ] **Step 8: Smoke-test the regenerated real bundle**

```powershell
dotnet run --project spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj -c Release -- `
  --bundle 'D:\fgo_unpack\fgo_assets\pet\mash\casual\manifest.json' `
  --renderer wpf --transparency conventional --scale 0.6 `
  --output 'D:\fgo_unpack\fgo_pet\.worktrees\story-pipeline\spikes\rendering\artifacts'
```

Expected: the body remains fixed, arrows replace only the upper overlay, panel toggles cover the lower body, and capture writes PNG plus JSONL.

- [ ] **Step 9: Commit the interactive probe**

```powershell
git add spikes/rendering
git commit -m "spike: add interactive Windows rendering probe"
```

---

### Task 7: Execute the staged matrix and bind the Phase 0 decision

**Files:**
- Modify: `.gitignore`
- Create: `spikes/rendering/artifacts/.gitkeep`
- Create: `spikes/rendering/README.md`
- Create: `docs/decisions/0001-windows-portrait-renderer.md`

**Interfaces:**
- Consumes: validated probe, captures, JSONL, and session summaries
- Produces: binding renderer and transparency choice plus Phase 1 constraints

- [ ] **Step 1: Ignore generated evidence**

Add:

```gitignore
spikes/rendering/artifacts/*
!spikes/rendering/artifacts/.gitkeep
```

- [ ] **Step 2: Document the staged manual procedure**

In `README.md`, specify exact launch commands and observation fields. Stage A compares WPF and Skia under conventional transparency at 60% for the four representative expressions and three Windows DPI values. Stage B compares conventional and DWM using the winning renderer. Stage C verifies the winner at 50%, 60%, and 75%, with panel hidden/dialogue/Todo states and the 280-switch stress run.

- [ ] **Step 3: Re-run machine verification before manual observation**

```powershell
& 'D:\environments\anaconda\python.exe' -m pytest -q
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release
dotnet build spikes/rendering/FgoPet.RenderingProbe.sln -c Release --no-restore
```

Expected: all Python and .NET checks pass.

- [ ] **Step 4: Execute renderer Stage A**

At Windows 100%, 125%, and 150%, capture WPF and Skia at 60% for `r01c01`, `r02c02`, `r04c04`, and `r07c03`. Record halo/fringe, hair and glasses clarity, overlay seam, anchor drift, switch flash, working set, and capture dimensions.

Choose WPF unless Skia is clearly better in at least two observed cells and remains within the 30% working-set rule.

- [ ] **Step 5: Execute transparency Stage B**

Using the winning renderer, compare conventional and DWM at the same three Windows DPI values with panel hidden and visible. Record per-pixel transparency, edge quality, drag behavior, click behavior, idle CPU/GPU, working set, and panel compositing.

Choose conventional unless DWM preserves correctness and provides a clear measured benefit.

- [ ] **Step 6: Execute final Stage C**

For the winning pair, test 50%, 60%, and 75%; run all 28 expressions; run 280 switches; capture dialogue and Todo panel states; drag the window repeatedly. Mark mixed-monitor rows `not-observed` without inference.

- [ ] **Step 7: Write the binding decision**

`docs/decisions/0001-windows-portrait-renderer.md` must contain:

- chosen renderer and transparency mode, or rejection of WPF;
- observed cells and unavailable mixed-monitor cells;
- evidence filenames;
- final overlay offset, body and overlay sizes, panel anchor, and default scale 0.50;
- exact DPI alignment and resource-disposal rules Phase 1 must retain;
- memory trend and visible limitations;
- whether the approved design requires amendment.

- [ ] **Step 8: Commit documentation and decision**

```powershell
git add .gitignore spikes/rendering/README.md spikes/rendering/artifacts/.gitkeep docs/decisions/0001-windows-portrait-renderer.md
git commit -m "docs: select Windows portrait rendering stack"
```

## Completion Gate

Phase 0 is complete only when Task 1's regenerated art is visually approved, every automated suite passes, Task 7 records an explicit renderer/composition choice, and unavailable mixed-DPI evidence is stated honestly. If no pair passes, stop before Phase 1 and create a new technology-spike design rather than weakening the acceptance rules.
