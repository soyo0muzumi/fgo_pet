# FGO Pet WPF Rendering Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select both a portrait renderer and a transparent-window composition mode that display Mash sharply and stably across Windows DPI scales and mixed-DPI monitors.

**Architecture:** Build a disposable .NET 8 WPF probe with two portrait renderers and two window-composition modes. The probe loads an external PNG supplied on the command line, displays an attached speech-bubble marker, records DPI/window diagnostics, and exports repeatable captures; automated tests cover image decoding, coordinate calculations, mode selection, and diagnostics serialization, while a manual matrix covers real desktop compositor behavior.

**Tech Stack:** C# 12, .NET SDK 8.0.121, .NET 8 WPF, xUnit, SkiaSharp 3.116.1, SkiaSharp.Views.WPF 3.116.1, Windows 10/11 per-monitor DPI awareness v2.

**Spec:** `docs/superpowers/specs/2026-08-25-fgo-pet-design.md`

**Reference:** `docs/references/vpet-rendering-notes.md`, based on Apache-2.0 VPet commit `b6f7b003`

## Global Constraints

- This is a disposable spike under `spikes/rendering`; no probe code may be copied into the production app without a separate reviewed plan.
- The spike must run on Windows with .NET 8 and must not require Python or a configured LLM.
- FGO art stays outside Git. The probe accepts `--asset <absolute-path>`; tests generate synthetic fixtures.
- Test 100%, 125%, and 150% scale, including movement between monitors with different scales when such hardware is available.
- Compare native WPF and SkiaSharp using the same source file, logical size, anchor calculations, and capture matrix.
- Compare conventional `AllowsTransparency=True` composition with a VPet-inspired DWM/window-chrome mode using `AllowsTransparency=False`, `GlassFrameThickness=-1`, and controlled `WS_EX_LAYERED` styles.
- Use per-monitor DPI awareness v2 and integer-aligned device pixels for the portrait bounds.
- Do not modify files under `D:\fgo_unpack\fgo_assets` or `D:\SteamLibrary`.
- Do not initialize or replace `D:\fgo_unpack\fgo_pet` during plan execution. First attach the directory to the intended GitHub checkout with explicit user approval.
- `dotnet --info` is unreliable on this machine because workload enumeration throws an installer exception. Use `dotnet --version`, `dotnet --list-sdks`, and `dotnet --list-runtimes` for environment checks.
- Every task ends with its focused tests passing and a Git commit; execution cannot begin until `git rev-parse --show-toplevel` succeeds.

## File Map

- `spikes/rendering/FgoPet.RenderingProbe.sln` — isolated spike solution.
- `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj` — WPF executable and renderer dependencies.
- `spikes/rendering/src/FgoPet.RenderingProbe/app.manifest` — per-monitor DPI awareness v2 declaration.
- `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml` — WPF application resources.
- `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml.cs` — argument parsing and startup failure handling.
- `spikes/rendering/src/FgoPet.RenderingProbe/ProbeOptions.cs` — immutable command-line options.
- `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/TransparencyMode.cs` — conventional WPF and DWM-layered mode selection.
- `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/LayeredWindowStyle.cs` — minimal HWND style hook inspired by VPet's public implementation.
- `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml` — transparent probe window, controls, portrait host, bubble marker.
- `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml.cs` — drag, renderer switch, scale switch, capture, and diagnostic recording.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/IRenderSurface.cs` — common renderer contract.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/RenderBackend.cs` — renderer enum and parser.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/WpfImageSurface.cs` — native WPF image renderer.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/SkiaImageSurface.cs` — SkiaSharp WPF renderer.
- `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/PixelAlignment.cs` — DPI-aware logical/device coordinate conversion.
- `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeSample.cs` — one measured frame/window sample.
- `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeRecorder.cs` — JSON Lines diagnostics writer.
- `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/CaptureWriter.cs` — PNG capture export.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj` — xUnit test project.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/SyntheticPng.cs` — generated transparent-edge fixture.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeOptionsTests.cs` — argument parsing tests.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/PixelAlignmentTests.cs` — DPI conversion tests.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/WpfImageSurfaceTests.cs` — source decoding and interpolation tests.
- `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeRecorderTests.cs` — diagnostics schema tests.
- `spikes/rendering/README.md` — exact build/run/capture instructions.
- `docs/decisions/0001-windows-portrait-renderer.md` — measured renderer decision and production constraints.

---

### Task 1: Create the isolated spike solution and command-line contract

**Files:**
- Create: `spikes/rendering/FgoPet.RenderingProbe.sln`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/ProbeOptions.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/RenderBackend.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/TransparencyMode.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeOptionsTests.cs`

**Interfaces:**
- Produces: `ProbeOptions.Parse(string[] args) -> ProbeOptions`
- Produces: `ProbeOptions(string AssetPath, RenderBackend Backend, TransparencyMode Transparency, double LogicalScale, string OutputDirectory)`
- Produces: CLI arguments `--asset`, `--renderer wpf|skia`, `--transparency wpf|dwm`, `--scale`, and `--output`

- [ ] **Step 1: Verify the execution prerequisites**

Run:

```powershell
git rev-parse --show-toplevel
dotnet --version
dotnet --list-runtimes
```

Expected: Git prints the intended `fgo_pet` checkout; .NET prints SDK `8.0.121`; the runtime list contains `Microsoft.WindowsDesktop.App 8.0.21`. Stop and resolve the checkout if the Git command fails.

- [ ] **Step 2: Write the failing option-parser tests**

Create tests covering defaults, explicit arguments, missing asset, invalid renderer, non-positive scale, and relative output normalization:

```csharp
[Fact]
public void Parse_reads_explicit_values()
{
    var options = ProbeOptions.Parse([
        "--asset", @"C:\art\mash.png",
        "--renderer", "skia",
        "--transparency", "dwm",
        "--scale", "1.25",
        "--output", @"C:\captures"
    ]);

    Assert.Equal(@"C:\art\mash.png", options.AssetPath);
    Assert.Equal(RenderBackend.Skia, options.Backend);
    Assert.Equal(TransparencyMode.DwmLayered, options.Transparency);
    Assert.Equal(1.25, options.LogicalScale);
    Assert.Equal(@"C:\captures", options.OutputDirectory);
}

[Theory]
[InlineData("unknown")]
[InlineData("")]
public void Parse_rejects_unknown_renderer(string value)
{
    var args = new[] { "--asset", @"C:\art\mash.png", "--renderer", value };
    Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(args));
}

[Theory]
[InlineData("0")]
[InlineData("-1")]
public void Parse_rejects_non_positive_scale(string value)
{
    var args = new[] { "--asset", @"C:\art\mash.png", "--scale", value };
    Assert.Throws<ArgumentOutOfRangeException>(() => ProbeOptions.Parse(args));
}
```

- [ ] **Step 3: Run the focused tests and confirm the expected failure**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter ProbeOptionsTests
```

Expected: compilation fails because `ProbeOptions` and `RenderBackend` do not exist.

- [ ] **Step 4: Implement the minimal option parser and WPF entry point**

Use an immutable record and parse with invariant culture:

```csharp
public sealed record ProbeOptions(
    string AssetPath,
    RenderBackend Backend,
    TransparencyMode Transparency,
    double LogicalScale,
    string OutputDirectory)
{
    public static ProbeOptions Parse(string[] args)
    {
        var values = args
            .Select((value, index) => (value, index))
            .Where(x => x.value.StartsWith("--", StringComparison.Ordinal))
            .ToDictionary(
                x => x.value,
                x => x.index + 1 < args.Length ? args[x.index + 1] : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("--asset", out var asset) || string.IsNullOrWhiteSpace(asset))
            throw new ArgumentException("--asset <absolute-png-path> is required.");

        var backend = RenderBackendParser.Parse(values.GetValueOrDefault("--renderer", "wpf"));
        var transparency = TransparencyModeParser.Parse(values.GetValueOrDefault("--transparency", "dwm"));
        var scaleText = values.GetValueOrDefault("--scale", "1.0");
        if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0)
            throw new ArgumentOutOfRangeException("--scale", "Scale must be positive.");

        var output = Path.GetFullPath(values.GetValueOrDefault("--output", "captures"));
        return new(Path.GetFullPath(asset), backend, transparency, scale, output);
    }
}
```

`App.OnStartup` must catch parse errors, show one `MessageBox`, set exit code 2, and avoid opening the probe window.

Define the backend parser in the same task so the scaffold compiles:

```csharp
public enum RenderBackend { Wpf, Skia }

public static class RenderBackendParser
{
    public static RenderBackend Parse(string value) => value.ToLowerInvariant() switch
    {
        "wpf" => RenderBackend.Wpf,
        "skia" => RenderBackend.Skia,
        _ => throw new ArgumentException($"Unknown renderer: {value}", nameof(value))
    };
}

public enum TransparencyMode { ConventionalWpf, DwmLayered }

public static class TransparencyModeParser
{
    public static TransparencyMode Parse(string value) => value.ToLowerInvariant() switch
    {
        "wpf" => TransparencyMode.ConventionalWpf,
        "dwm" => TransparencyMode.DwmLayered,
        _ => throw new ArgumentException($"Unknown transparency mode: {value}", nameof(value))
    };
}
```

- [ ] **Step 5: Run the focused tests and the full empty-shell test suite**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj
```

Expected: all `ProbeOptionsTests` pass.

- [ ] **Step 6: Commit the isolated scaffold**

```powershell
git add spikes/rendering
git commit -m "spike: scaffold Windows rendering probe"
```

---

### Task 2: Add DPI-aware pixel alignment and diagnostic records

**Files:**
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/PixelAlignment.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeSample.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/ProbeRecorder.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/PixelAlignmentTests.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/ProbeRecorderTests.cs`

**Interfaces:**
- Consumes: `RenderBackend` and `RenderBackendParser` from Task 1
- Produces: `readonly record struct PixelSize(int Width, int Height)`
- Produces: `PixelAlignment.AlignLogicalRect(Rect logical, DpiScale dpi) -> Rect`
- Produces: `PixelAlignment.ToDevicePixels(Size logical, DpiScale dpi) -> PixelSize`
- Produces: `ProbeRecorder.AppendAsync(ProbeSample sample, CancellationToken token) -> Task`

- [ ] **Step 1: Write failing pixel-alignment tests**

```csharp
[Theory]
[InlineData(1.0, 10.4, 20.6, 100.2, 200.2, 10, 21, 100, 200)]
[InlineData(1.25, 10.4, 20.6, 100.2, 200.2, 10.4, 20.8, 100.0, 200.0)]
[InlineData(1.5, 10.4, 20.6, 100.2, 200.2, 10.6666667, 20.6666667, 100.0, 200.0)]
public void AlignLogicalRect_rounds_edges_to_device_pixels(
    double scale, double x, double y, double width, double height,
    double expectedX, double expectedY, double expectedWidth, double expectedHeight)
{
    var actual = PixelAlignment.AlignLogicalRect(
        new Rect(x, y, width, height), new DpiScale(scale, scale));

    Assert.Equal(expectedX, actual.X, 5);
    Assert.Equal(expectedY, actual.Y, 5);
    Assert.Equal(expectedWidth, actual.Width, 5);
    Assert.Equal(expectedHeight, actual.Height, 5);
}
```

- [ ] **Step 2: Run tests and confirm missing-type failures**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter "PixelAlignmentTests|ProbeRecorderTests"
```

Expected: compilation fails for missing `PixelAlignment`, `ProbeSample`, and `ProbeRecorder`.

- [ ] **Step 3: Implement alignment using rounded device-space edges**

```csharp
public static Rect AlignLogicalRect(Rect logical, DpiScale dpi)
{
    static double Align(double value, double scale) => Math.Round(value * scale) / scale;

    var left = Align(logical.Left, dpi.DpiScaleX);
    var top = Align(logical.Top, dpi.DpiScaleY);
    var right = Align(logical.Right, dpi.DpiScaleX);
    var bottom = Align(logical.Bottom, dpi.DpiScaleY);
    return new Rect(left, top, right - left, bottom - top);
}

public static PixelSize ToDevicePixels(Size logical, DpiScale dpi) => new(
    checked((int)Math.Round(logical.Width * dpi.DpiScaleX)),
    checked((int)Math.Round(logical.Height * dpi.DpiScaleY)));

public readonly record struct PixelSize(int Width, int Height);
```

Define `ProbeSample` with timestamp, renderer, asset path, source pixel size, logical portrait bounds, device portrait size, monitor device name, DPI X/Y, window left/top, process working set, capture path, and free-form note. `ProbeRecorder` must append one camelCase JSON object per line using UTF-8 without retaining an open file handle.

- [ ] **Step 4: Test JSON Lines stability and secret-free output**

The recorder test must write two samples to a temporary directory, read two lines back, assert both parse as JSON, assert `renderer` and `dpiX` values, and assert no property named `apiKey`, `prompt`, or `chatText` exists.

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter "PixelAlignmentTests|ProbeRecorderTests"
```

Expected: all focused tests pass.

- [ ] **Step 5: Commit coordinate and diagnostic contracts**

```powershell
git add spikes/rendering
git commit -m "spike: add DPI alignment and render diagnostics"
```

---

### Task 3: Implement and test the native WPF image surface

**Files:**
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/IRenderSurface.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/WpfImageSurface.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/SyntheticPng.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/StaTest.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/WpfImageSurfaceTests.cs`

**Interfaces:**
- Produces: `IRenderSurface.View -> FrameworkElement`
- Produces: `IRenderSurface.SourcePixelSize -> PixelSize`
- Produces: `IRenderSurface.Load(string absolutePngPath) -> void`
- Produces: `IRenderSurface.SetLogicalSize(Size size) -> void`
- Produces: `IRenderSurface.Capture(DpiScale dpi) -> BitmapSource`

- [ ] **Step 1: Generate a synthetic transparent-edge fixture in tests**

`SyntheticPng.Create(path)` must create a 64×64 BGRA PNG with fully transparent outer pixels, a one-pixel semi-transparent red border, and an opaque red 32×32 center. Use `WriteableBitmap`, `PngBitmapEncoder`, and a temporary test directory; do not add binary fixtures to Git.

- [ ] **Step 2: Write failing WPF surface tests**

```csharp
[Fact]
public void Load_decodes_at_source_resolution_and_freezes_bitmap()
{
    StaTest.Run(() =>
    {
        var path = SyntheticPng.Create();
        var surface = new WpfImageSurface();

        surface.Load(path);

        Assert.Equal(new PixelSize(64, 64), surface.SourcePixelSize);
        var image = Assert.IsType<Image>(surface.View);
        var source = Assert.IsAssignableFrom<BitmapSource>(image.Source);
        Assert.True(source.IsFrozen);
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
    });
}
```

Put WPF assertions inside a normal xUnit test that creates and joins an STA thread with this helper:

```csharp
public static class StaTest
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
```

Use the same `StaTest.Run` wrapper for every test that constructs WPF controls. This defines the STA behavior without a custom xUnit discoverer.

- [ ] **Step 3: Run the focused test and confirm failure**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter WpfImageSurfaceTests
```

Expected: compilation fails because `IRenderSurface` and `WpfImageSurface` do not exist.

- [ ] **Step 4: Implement WPF decoding and rendering**

Decode with `BitmapCacheOption.OnLoad`, close the file immediately, freeze the bitmap, set `Stretch.Uniform`, `SnapsToDevicePixels=true`, `UseLayoutRounding=true`, and `BitmapScalingMode.HighQuality`. `SetLogicalSize` must set explicit `Width` and `Height` after calling `PixelAlignment.AlignLogicalRect` at window layout time.

`Capture` must render the view through `RenderTargetBitmap` at `Math.Round(ActualWidth * dpi.DpiScaleX)` by `Math.Round(ActualHeight * dpi.DpiScaleY)` pixels and preserve the transparent background.

- [ ] **Step 5: Run renderer tests and inspect the exported synthetic capture**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter WpfImageSurfaceTests
```

Expected: all WPF surface tests pass; the capture retains an alpha channel, opaque center pixels, and semi-transparent border pixels.

- [ ] **Step 6: Commit the native WPF renderer**

```powershell
git add spikes/rendering
git commit -m "spike: add native WPF portrait renderer"
```

---

### Task 4: Implement and test the SkiaSharp surface

**Files:**
- Modify: `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Rendering/SkiaImageSurface.cs`
- Create: `spikes/rendering/tests/FgoPet.RenderingProbe.Tests/SkiaImageSurfaceTests.cs`

**Interfaces:**
- Consumes: `IRenderSurface`, `PixelAlignment`, and `PixelSize`
- Produces: `SkiaImageSurface : IRenderSurface`

- [ ] **Step 1: Add pinned Skia packages and write the failing tests**

Add exact package references:

```xml
<PackageReference Include="SkiaSharp" Version="3.116.1" />
<PackageReference Include="SkiaSharp.Views.WPF" Version="3.116.1" />
```

Tests must load the synthetic PNG, assert `SourcePixelSize == 64×64`, set a 96×96 logical size, capture at 1.25 DPI, and assert a 120×120 output bitmap with non-zero alpha in the center.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter SkiaImageSurfaceTests
```

Expected: compilation fails because `SkiaImageSurface` does not exist.

- [ ] **Step 3: Implement Skia rendering with explicit sampling**

Load the file into an immutable `SKImage`, release previous native resources on reload, and render through `SKElement.PaintSurface`. Clear with transparent color, compute a centered uniform-fit destination rectangle, align destination edges to whole device pixels, and draw using cubic sampling:

```csharp
canvas.Clear(SKColors.Transparent);
canvas.DrawImage(
    _image,
    sourceRect,
    destinationRect,
    new SKSamplingOptions(SKCubicResampler.Mitchell),
    paint);
```

Implement `IDisposable`; tests must verify that loading a second image and disposing the surface do not throw.

- [ ] **Step 4: Run both renderer suites**

Run:

```powershell
dotnet test spikes/rendering/tests/FgoPet.RenderingProbe.Tests/FgoPet.RenderingProbe.Tests.csproj --filter "WpfImageSurfaceTests|SkiaImageSurfaceTests"
```

Expected: both backends pass the same source-size, output-size, and alpha-retention assertions.

- [ ] **Step 5: Commit the Skia comparison backend**

```powershell
git add spikes/rendering
git commit -m "spike: add SkiaSharp portrait renderer"
```

---

### Task 5: Build the transparent interactive probe window

**Files:**
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/app.manifest`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Windowing/LayeredWindowStyle.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/MainWindow.xaml.cs`
- Create: `spikes/rendering/src/FgoPet.RenderingProbe/Diagnostics/CaptureWriter.cs`
- Modify: `spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj`
- Modify: `spikes/rendering/src/FgoPet.RenderingProbe/App.xaml.cs`

**Interfaces:**
- Consumes: `ProbeOptions`, `IRenderSurface`, `WpfImageSurface`, `SkiaImageSurface`, `PixelAlignment`, `ProbeRecorder`
- Produces: keyboard controls `F1` WPF renderer, `F2` Skia renderer, `1` 0.75×, `2` 1.0×, `3` 1.25×, `4` 1.5×, `C` capture, `Esc` exit
- Produces: `<output>/samples.jsonl` and `<output>/captures/<timestamp>-<renderer>-<dpi>.png`

- [ ] **Step 1: Declare per-monitor DPI awareness v2**

Set the project `ApplicationManifest` to `app.manifest` and include:

```xml
<windowsSettings>
  <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
</windowsSettings>
```

- [ ] **Step 2: Implement both transparent-window composition modes**

Conventional mode creates the window with `AllowsTransparency=True`, `WindowStyle=None`, and `Background=Transparent`.

DWM-layered mode creates the window with `AllowsTransparency=False`, `WindowStyle=None`, `Background=null`, and `WindowChrome.GlassFrameThickness=-1`. At `SourceInitialized`, `LayeredWindowStyle` installs an HWND hook for `WM_STYLECHANGING` and preserves `WS_EX_LAYERED` for `GWL_EXSTYLE`. The hook must be removed on close. Do not copy VPet's full Win32 helper; declare only `STYLESTRUCT`, `GWL_EXSTYLE`, `WS_EX_LAYERED`, and the required hook logic.

Because WPF does not permit changing `AllowsTransparency` after the HWND is created, transparency mode is selected through `--transparency` and compared in separate probe processes.

- [ ] **Step 3: Build the transparent window layout**

Use `WindowStyle=None`, `SizeToContent=WidthAndHeight`, `UseLayoutRounding=True`, and `SnapsToDevicePixels=True`, with transparency properties supplied by the selected composition mode. The root grid contains:

- a portrait host with fixed logical width 360 DIP;
- a speech-bubble test marker anchored 24 DIP from the portrait's top-right visible bound;
- a diagnostic overlay showing backend, monitor, DPI, logical size, device size, and process working set;
- no shadow or blur effect, because either can contaminate edge comparison.

- [ ] **Step 4: Implement drag, renderer switching, scaling, and DPI change handling**

Use `DragMove()` on left-button drag. Add an `HwndSource` hook and process `WM_DPICHANGED` (`0x02E0`); after the message, schedule one `DispatcherPriority.Loaded` callback to re-align portrait bounds, reposition the bubble marker, refresh the overlay, and append a `ProbeSample`. Switching backend must preserve the same asset, logical portrait size, window location, and scale.

- [ ] **Step 5: Implement deterministic capture and diagnostics output**

On `C`, wait for `DispatcherPriority.Render`, capture the portrait plus bubble, save PNG through `PngBitmapEncoder`, then append a sample containing the capture path. File names use UTC timestamp with filesystem-safe separators and invariant DPI values.

- [ ] **Step 6: Build and run against the real Mash asset**

Run:

```powershell
dotnet build spikes/rendering/FgoPet.RenderingProbe.sln -c Release
dotnet run --project spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj -c Release -- --asset "D:\fgo_unpack\fgo_assets\servant\800100\8001000_merged.png" --renderer wpf --transparency dwm --scale 1.0 --output "D:\fgo_unpack\fgo_pet\spikes\rendering\artifacts"
```

Expected: a transparent Mash window opens; dragging moves portrait and bubble together; F1/F2 switch renderer without changing logical size; C writes a PNG and one valid JSON Lines record containing the renderer and transparency mode.

- [ ] **Step 7: Commit the interactive probe**

```powershell
git add spikes/rendering
git commit -m "spike: add interactive mixed-DPI render probe"
```

---

### Task 6: Execute the DPI matrix and record the renderer decision

**Files:**
- Create: `spikes/rendering/README.md`
- Create: `docs/decisions/0001-windows-portrait-renderer.md`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: probe executable, `samples.jsonl`, and PNG captures
- Produces: a binding renderer choice and production rendering constraints

- [ ] **Step 1: Document the exact manual matrix**

The README must instruct the reviewer to capture all four renderer/composition combinations at portrait scales 0.75×, 1.0×, 1.25×, and 1.5× under Windows scales 100%, 125%, and 150%. For mixed-DPI setups, repeat after dragging the window from monitor A to B and back to A without restarting.

For each cell, record:

- visible halo, black fringe, or blur;
- one-pixel edge continuity around hair, shield, and clothing;
- bubble anchor drift in physical pixels;
- logical and device capture dimensions;
- working set after ten backend switches;
- flicker during expression-equivalent image reload;
- whether clarity recovers after cross-monitor movement.

- [ ] **Step 2: Add artifact ignore rules**

Ignore generated captures and JSON Lines while retaining directory documentation:

```gitignore
spikes/rendering/artifacts/*
!spikes/rendering/artifacts/.gitkeep
```

- [ ] **Step 3: Run automated verification before the manual matrix**

Run:

```powershell
dotnet test spikes/rendering/FgoPet.RenderingProbe.sln -c Release
dotnet build spikes/rendering/FgoPet.RenderingProbe.sln -c Release --no-restore
```

Expected: all tests pass and build exits 0 with no warnings introduced by spike code.

- [ ] **Step 4: Execute and archive the manual observations locally**

Run the matrix using the real Mash asset. Keep raw captures under ignored `spikes/rendering/artifacts`; summarize measurements in the decision document. If only one physical monitor is available, mark mixed-DPI rows as `not-observed` and do not infer a result from same-monitor scaling.

- [ ] **Step 5: Apply the explicit decision rule**

Select the renderer first: choose native WPF when it has no visible edge regression against SkiaSharp in any observed cell and its bubble drift is at most one physical pixel. Choose SkiaSharp only when it is visibly cleaner in at least two scale cells without exceeding WPF working set by more than 30% or regressing after cross-monitor movement.

Select the composition mode independently: choose DWM-layered when it preserves correct per-pixel transparency and does not regress edge quality while using less working set or lower idle GPU/CPU than conventional WPF transparency. Choose conventional WPF only when DWM-layered has visible artifacts, unreliable click behavior, or unstable mixed-DPI movement. If no renderer/composition pair passes, reject WPF as the production shell and open a new technology-spike spec; do not tune thresholds after seeing results.

The decision document must state:

- chosen portrait renderer and transparency composition mode, or rejection;
- observed matrix and unavailable rows;
- evidence file names;
- exact pixel-alignment and DPI rules production code must retain;
- measured limitations;
- whether the approved main specification requires amendment.

- [ ] **Step 6: Commit the verified decision**

```powershell
git add .gitignore spikes/rendering/README.md spikes/rendering/artifacts/.gitkeep docs/decisions/0001-windows-portrait-renderer.md
git commit -m "docs: select Windows portrait renderer"
```

## Plan Sequence After This Spike

Create the remaining implementation plans only after Task 6 selects the production renderer:

1. Desktop shell and servant resource-package plan.
2. Event center, attached pomodoro, timeline, and bond plan.
3. OpenAI-compatible dialogue, prompt, memory, and lore-retrieval plan.
4. Codex plugin, Skill, MCP bridge, privacy, and offline-queue plan.
5. Application awareness, backup, packaging, and distribution-readiness plan.

Each subsequent plan must cite the approved design specification and `docs/decisions/0001-windows-portrait-renderer.md`.
