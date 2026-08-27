# Phase 0 Windows Rendering Probe

This disposable Windows 11 probe compares layered Mash portrait rendering and transparent-window composition. It consumes the validated schema-v2 bundle; it does not extract or modify FGO assets.

## Prerequisites

- Windows 11
- .NET SDK 8.0.121 and Windows Desktop Runtime 8.x
- Bundle QA status `PASS` at `D:\fgo_unpack\fgo_assets\pet\mash\casual`
- Change Windows display scale manually between stages; sign out only if Windows requests it

## Controls

- `Left` / `Right`: previous or next expression
- `1` / `2` / `3`: 50%, 60%, or 75% uniform scale
- `F1` / `F2`: WPF or Skia backend
- `P`: show or hide the terminal panel
- `T`: dialogue or Todo panel sample
- `C`: capture the complete transparent composition
- `R`: run 280 expression switches and write the session summary
- `Esc`: close

Outputs are written under `spikes/rendering/artifacts` and ignored by Git except for `.gitkeep`.

## Launch commands

Run from the repository worktree. Replace `<renderer>` with `wpf` or `skia`, and `<transparency>` with `conventional` or `dwm`.

```powershell
dotnet run --project spikes/rendering/src/FgoPet.RenderingProbe/FgoPet.RenderingProbe.csproj -c Release --no-build -- `
  --bundle 'D:\fgo_unpack\fgo_assets\pet\mash\casual\manifest.json' `
  --renderer <renderer> --transparency <transparency> --scale 0.6 `
  --output 'D:\fgo_unpack\fgo_pet\.worktrees\story-pipeline\spikes\rendering\artifacts'
```

## Observation fields

For every observed cell record: Windows scale, renderer, transparency mode, portrait scale, expression ID, panel state, halo/fringe, hair and glasses clarity, overlay seam, anchor drift, switch flash, capture dimensions, working set, and notes. Record unavailable mixed-monitor movement as `not-observed`; do not infer it.

## Stage A — renderer

At Windows 100%, 125%, 150%, and the device-default 200%, use conventional transparency and 60% portrait scale. Compare WPF and Skia for `r01c01`, `r02c02`, `r04c04`, and `r07c03`; press `C` for each cell.

Choose WPF unless Skia is visibly better in at least two observed cells and its working set is no more than approximately 30% above WPF.

## Stage B — transparency

With the winning renderer, compare conventional and DWM at Windows 100%, 125%, 150%, and the device-default 200%, with the panel hidden and visible. Record per-pixel transparency, edge quality, drag/click behavior, idle CPU/GPU, working set, and panel compositing.

Choose conventional unless DWM preserves correctness and has a clear measured benefit.

## Stage C — final combination

With the winning pair, test 50%, 60%, and 75%; visit all 28 expressions; capture dialogue and Todo panel states; drag the window repeatedly; press `R` once and verify `session-summary.json` reports 280 switches. Mixed-monitor rows remain `not-observed` on the current single-monitor setup.
