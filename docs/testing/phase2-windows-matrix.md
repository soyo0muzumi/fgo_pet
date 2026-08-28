# Phase 2 Windows Manual Matrix

Each cell records `pass`, `fail`, or `not-observed` plus an evidence path
(screenshot under `docs/evidence/phase2/` or a short note). Never infer
mixed-monitor results — observe every row on the listed setup.

Setups: **150%** (single monitor 150% DPI), **200%** (single monitor 200% DPI),
**Mixed** (150% + 100% two-monitor arrangement, portrait moved across the seam).

| # | Scenario | 150% | 200% | Mixed | Evidence |
|---|----------|------|------|-------|----------|
| 1 | Idle Compact shows character text, no timer | | | | |
| 2 | Focusing Compact replaces text with countdown timer | | | | |
| 3 | Paused Compact shows paused phase label and frozen time | | | | |
| 4 | Break Compact shows break label | | | | |
| 5 | Focus column stretches the same panel; portrait does not move | | | | |
| 6 | Today column stretches; portrait does not move | | | | |
| 7 | Todo column (empty) stretches; portrait does not move | | | | |
| 8 | Dialogue column stretches; portrait does not move | | | | |
| 9 | Portrait position identical before/after every stretch | | | | |
| 10 | Portrait drag works in every panel state | | | | |
| 11 | Panel drag works in every panel state | | | | |
| 12 | Panel height stays within the 60% work-area cap while stretched | | | | |
| 13 | Panel flips to the alternate work area near screen edges | | | | |
| 14 | Built-in 25/5 × 4 preset starts, completes, records timeline + bond | | | | |
| 15 | Built-in 50/10 × 2 preset starts, completes, records timeline + bond | | | | |
| 16 | Custom preset validation: focus 4 rejected, 181 rejected, break 0/61 rejected, cycles 0/13 rejected | | | | |
| 17 | Custom preset 45/9 × 3 accepted and starts | | | | |
| 18 | Editing custom preset suppresses idle auto-collapse | | | | |
| 19 | Header shows exactly 专注 / 今日 / TODO / 对话, no collapse action | | | | |
| 20 | Portrait click closes the panel from every expanded state | | | | |
| 21 | Esc steps an expanded panel down to Compact | | | | |
| 22 | Normal exit keeps the running session; next start recovers paused with correct remaining seconds | | | | |
| 23 | Forced process kill during focus recovers paused with the last ≤30 s snapshot | | | | |
| 24 | Offline wall time (clock jump) never advances a recovered session | | | | |
| 25 | Focus completion commits event + timeline + bond atomically (25 min) | | | | |
| 26 | Focus completion commits atomically (50 min) | | | | |
| 27 | Servant switch during a stage: credit stays with the stage-start servant | | | | |
| 28 | Two servants accumulate independent bond totals and levels | | | | |
| 29 | Bond level-up emits timeline row and portrait expression change | | | | |
| 30 | Pack with valid `dialogue/`: themed text and expression appear | | | | |
| 31 | Pack without `dialogue/`: neutral status text appears | | | | |
| 32 | Pack with invalid `dialogue/` (bad expression): neutral fallback, no error dialog | | | | |

Acceptance: every required cell is `pass` with evidence before Phase 2 is marked
releasable. Until then release status stays **deferred**.
