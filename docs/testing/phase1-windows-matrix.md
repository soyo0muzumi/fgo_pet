# Phase 1 Windows manual matrix

Fill PASS/FAIL + evidence (screenshot file name or session log reference) under the
required real-device cells on Windows 11 (the device-default scaling here is 200%).
No cell may be reported as PASS from a test run alone; manual observation is required.
Phase 1 is not releasable while a required cell is `unobserved`.

Required machines: Windows 11 @ 200%, Windows 11 @ 150%.

## 200% (primary)

| Row | Requirement | Result | Evidence |
|---|---:|---|---|
| 1 | Packless startup: no pack -> tray + servant library, no portrait window | unobserved | |
| 2 | Local Mash `.fgopetpack` install -> offline portrait | unobserved | |
| 3 | Transparent pixels pass clicks through; portrait and panel draggable/right-clickable | unobserved | |
| 4 | Portrait drag saves placement; restart restores on the same monitor | unobserved | |
| 5 | Portrait scales 0.50 / 0.60 / 0.75 all render seam-free | unobserved | |
| 6 | 28-expression cycle with stable body visual and no size drift | unobserved | |
| 7 | Expression seams invisible at the device-default 200% | unobserved | |
| 8 | Pack failure recovery preserves the current portrait | unobserved | |
| 9 | Expanded panel stays <= 60% of work area and doesn't intercept an empty window | unobserved | |
| 10 | Tray restores a hidden window; tray Exit exits the process | unobserved | |
| 11 | Second launch activates the first instance; `.fgopetpack` path is forwarded | unobserved | |
| 12 | Diagnostics show stable error codes + relative paths, no absolute paths | unobserved | |

## 150% (final combination)

| Row | Requirement | Result | Evidence |
|---|---:|---|---|
| 13 | Final WPF+conventional combination at 150% renders and drags correctly | unobserved | |

## Mixed-DPI dual monitors (when available)

| Row | Requirement | Result | Evidence |
|---|---:|---|---|
| 14 | Cross-screen drag keeps the portrait usable after Windows display changes | unobserved | |
| 15 | Negative screen coordinates / monitor disconnect -> window stays visible | unobserved | |