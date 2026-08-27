# ADR 0001: Windows portrait renderer

Status: accepted

Date: 2026-08-27

## Context

Phase 0 selects a renderer and transparent-window mode for a stable `full_body` base plus one independently replaceable 256×240 expression overlay. The device-default 200% scale received the complete comparison matrix; 150% received final-combination verification. The user explicitly waived 100% and 125% because the desktop becomes too small for the intended use.

## Fixed composition contract

- Body: `full_body`, 303×603 source pixels
- Expression overlay: one of `r01c01`–`r07c04`, 256×240 source pixels
- Overlay offset: `(13, 0)` source pixels, verified by pixel overlap and manual drawing alignment
- Panel anchor: `(151, 360)` source pixels
- Default uniform scale: `0.50`, selected in final 200% DPI review; supported optional scales: `0.60`, `0.75`
- Logical and device body edges, overlay edges, bottom anchor, and panel anchor derive from one source→logical→device transform
- Decoded WPF bitmaps are `OnLoad` and frozen; Skia images are disposed on reload and close

## Machine evidence

- Python suite: 105 passed
- .NET suite: 20 passed
- Release build: passed with 0 warnings and 0 errors
- Current display setting: 200% at 2880×1800 (Windows recommended setting; user screenshot)
- `GetDpiForSystem()` returned a virtualized 96 DPI value and was rejected as evidence for the physical display setting
- Real schema-v2 bundle startup: passed with WPF/conventional/60%; process remained healthy until the smoke test ended
- Mixed-monitor movement: `not-observed` and non-blocking

## Observation matrix

| Stage | Windows scale | Candidate | Result | Evidence |
|---|---:|---|---|---|
| A | 200% | WPF vs Skia, conventional, 60% | no visible clarity difference; WPF selected by default rule; `(13,0)` seam-free | manual window review; Windows display-settings screenshot |
| A | 150% | final WPF + conventional combination at 50% | clarity, expression switching, panel modes and dragging correct | `GetDpiForWindow=144`; manual window review |
| A | 125% | not observed | desktop display too small for intended use; waived by user | user decision |
| A | 100% | not observed | desktop display too small for intended use; waived by user | user decision |
| B | 200% | conventional vs DWM | both transparent, edge/panel/drag correct; DWM has no clear benefit, so conventional selected | manual window review; Windows display-settings screenshot |
| B | 150% | not repeated | final conventional combination stable; further comparisons waived | user decision |
| B | 125% / 100% | not observed | display scales unsuitable for intended use | user decision |
| C | 200% | WPF + conventional, 50/60/75%, 28 expressions, 280 switches | 50% selected; expressions seam-free; panel toggle, dialogue/Todo, drag and stress run passed | manual review; `samples.jsonl`; `session-summary.json` |

## Decision rule

Select WPF unless Skia is visibly better in at least two observed cells without exceeding WPF working set by more than approximately 30%. Select conventional `AllowsTransparency=True` unless DWM preserves correct behavior and provides a clear measured benefit.

## Decision

Use native WPF layered images with conventional `AllowsTransparency=True`. Use one frozen 303×603 `full_body` image and replace only the independently frozen 256×240 expression source at `(13,0)`. The production default uniform scale is 0.50.

Skia remains rejected for Phase 1 because it showed no visible clarity advantage at the device-default 200% scale and adds native-resource and dependency complexity. DWM remains rejected because it showed no functional or visual advantage over conventional transparency.

Phase 1 must preserve the shared source→logical→device transform, per-monitor DPI response, decoded-file release, stable body visual, and explicit native-resource disposal rules validated by the spike.

### Observed limitation

The Phase 0 panel uses static dialogue and Todo samples. Dynamic content growth is intentionally unresolved: Phase 1 must define maximum visible rows, overflow scrolling or paging, maximum panel height, and the allowed portrait-occlusion boundary before implementing live lists.

The 200% DPI 280-switch run recorded 157.7 MiB minimum, 173.8 MiB maximum, 173.3 MiB final, and 172.6 MiB post-GC working set. No visible failure or unbounded trend was observed, but the small post-GC reduction remains a spike limitation to revisit in the production implementation.
