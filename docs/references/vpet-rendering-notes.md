# VPet Rendering Reference Notes

Reference repository: `https://github.com/LorisYounger/VPet.git`

Reviewed commit: `b6f7b003`

Local reference checkout: `D:\fgo_unpack\VPet`

License: Apache License 2.0

## Relevant findings

### Static portraits use WPF Image

`VPet-Simulator.Core/Graph/Picture.cs` creates reusable WPF `Image` controls and assigns a `BitmapImage` to `Image.Source`. SkiaSharp is used in this path to inspect PNG frame counts; it is not the final static display surface.

Implication for FGO Pet: native WPF `Image` remains the primary baseline for static Mash portraits. SkiaSharp should not be assumed to improve final presentation merely because VPet references it.

### SkiaSharp preprocesses animated resources

`VPet-Simulator.Core/Graph/PNGAnimation.cs` uses SkiaSharp to decode frames, resize them to a configured maximum resolution, combine them into sprite sheets, and write caches. The resulting frames are exposed to WPF as `BitmapSource` instances.

Implication for FGO Pet: SkiaSharp is a useful optional preprocessing tool for asset normalization, but the first release's static portrait switching does not require VPet's animation cache architecture.

### Windows transparency avoids the conventional WPF path

`VPet-Simulator.Windows/MainWindow.xaml` uses a borderless, size-to-content window with a null background and `WindowChrome GlassFrameThickness="-1"`. On Windows, the constructor does not set `AllowsTransparency=True`.

`MainWindow.xaml.cs` installs an HWND hook that preserves `WS_EX_LAYERED`. Its comments state that WPF's `AllowsTransparency=True` path uses a lower-performance built-in implementation. Mouse pass-through is toggled independently with `WS_EX_TRANSPARENT`.

Implication for FGO Pet: the rendering spike must compare two window-composition modes, not only two bitmap renderers:

1. Conventional WPF transparency using `AllowsTransparency=True`.
2. VPet-style DWM/window-chrome transparency using `AllowsTransparency=False`, glass extension, and controlled extended styles.

### DPI-aware screen bounds are converted explicitly

`VPet-Simulator.Windows/Function/MWController.cs` gets the active monitor from the HWND and divides physical screen bounds by `CompositionTarget.TransformToDevice` to obtain WPF logical bounds. Zoom changes are applied by resizing the main grid rather than applying a render transform.

Implication for FGO Pet: monitor bounds, window placement, attached timer position, and recovery-to-visible-area logic must be expressed in one declared coordinate space and converted at HWND boundaries.

## What to borrow

- Separation between Core, Windows shell, and public interface responsibilities.
- Native WPF image display as the static baseline.
- DWM/window-chrome transparency as a measured candidate.
- Explicit HWND-to-monitor DPI conversion.
- Independent use of `WS_EX_TRANSPARENT` for click-through.
- Resource caching and reuse ideas where profiling proves they are needed.

## What not to copy into the spike

- Animation state machinery, sprite-sheet building, game simulation, economy, Steam integration, or mod verification.
- Fixed 500-DIP assumptions.
- Broad exception swallowing around image decoding.
- Source code copied verbatim. The probe should independently implement only the minimal measured behavior and retain an Apache-2.0 attribution if any copyrightable implementation is later adapted.

## Plan impact

The rendering spike now has two independent comparison axes:

- portrait renderer: native WPF vs SkiaSharp WPF surface;
- transparency composition: conventional WPF vs VPet-style DWM layered window.

The production decision must select both a renderer and a composition mode.
