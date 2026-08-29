# Phase 2 Terminal UI and Release Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Match the approved terminal-instrument HTML inside the existing WPF attached panel, produce a testable Release build, and prepare Phase 2 completion evidence plus the Phase 3 handoff.

**Architecture:** Keep the existing attached-panel state machine, anchor, window and Phase 2 services. Restructure only `AttachedPanelView` and add presentation-only ViewModel properties/commands needed by the approved grid; fix interactive-surface hit testing at the window coordinator boundary.

**Tech Stack:** C# 12, .NET 8, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-28-phase2-events-focus-timeline-design.md`

## Global Constraints

- Preserve the existing portrait, panel anchor, clipped corners, 220–340 DIP width and 60% work-area height cap.
- Keep the header exactly `专注 | 今日 | TODO | 对话`; no collapse button.
- Keep TODO empty and do not add Phase 3 behavior.
- Custom focus is 5–180 minutes, break is 1–60 minutes, cycles are 1–12; keyboard input and step buttons must both work.
- Compact timer must allow pause/resume and exit without changing the portrait position.
- Follow TDD and keep the full Phase 1/2 suite green.

---

### Task 1: Presentation Contract

**Files:**
- Modify: `tests/FgoPet.App.Tests/Panels/Phase2AttachedPanelViewModelTests.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs`

**Interfaces:**
- Produces: read-only `CycleText`, `TimerMetaText`, `ProgressPercent`, and `CustomTotalText`; methods `AdjustCustomFocus(int)`, `AdjustCustomBreak(int)`, and `AdjustCustomCycles(int)`.

- [ ] Add failing tests for timer labels, progress, total duration excluding the final break, and bounded step changes.
- [ ] Run the targeted tests and confirm the missing contract fails.
- [ ] Add the smallest presentation properties and bounded adjustment methods backed by the existing focus session/preset state.
- [ ] Re-run the targeted tests and confirm they pass.

### Task 2: Interactive Surface Boundary

**Files:**
- Modify: `tests/FgoPet.App.Tests/Windowing/InteractiveSurfaceTests.cs`
- Create: `src/FgoPet.App/Windowing/InteractiveSurface.cs`
- Modify: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs`

**Interfaces:**
- Produces: `InteractiveSurface.Contains(DependencyObject?)`, true for buttons, text inputs, selectors and scroll bars or their descendants.

- [ ] Add failing STA tests proving text boxes, buttons and scroll bars are interactive while a plain panel background is not.
- [ ] Run the targeted tests and confirm failure because the classifier is missing.
- [ ] Implement the classifier and replace the coordinator's button-only ancestor check.
- [ ] Re-run the targeted tests and confirm they pass.

### Task 3: Approved Terminal-Instrument XAML

**Files:**
- Modify: `tests/FgoPet.Windows.Tests/Panels/AttachedPanelViewIntegrationTests.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml`
- Modify: `src/FgoPet.App/Panels/AttachedPanelView.xaml.cs`

**Interfaces:**
- Consumes: Task 1 presentation contract and Task 2 input boundary.
- Produces: the approved idle compact, expanded focus editor and running compact layouts.

- [ ] Add failing structure/state tests for the three-column preset row, named step buttons and text fields, running timer progress/round labels, and state-specific ornaments.
- [ ] Run the Windows tests and confirm failure on missing named elements.
- [ ] Rebuild styles and grids to match the approved HTML while retaining existing click handlers and visibility state flow.
- [ ] Wire step controls to Task 1 methods and keep direct text entry bindings.
- [ ] Run Windows and App tests and confirm all pass.

### Task 4: Release Gate and Handoff

**Files:**
- Modify: `docs/testing/phase2-windows-matrix.md`
- Create: `docs/reports/2026-08-28-phase2-handoff.md`
- Modify: `README.md`

**Interfaces:**
- Produces: Release output under `artifacts/release/` and a Phase 3-ready handoff that keeps manual Windows acceptance pending until user confirmation.

- [ ] Run `pwsh -NoProfile -File scripts/test-phase2.ps1` and record automated evidence.
- [ ] Publish with `dotnet publish src/FgoPet.App/FgoPet.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/release/FgoPet-win-x64`.
- [ ] Document build path, known manual checks, SQLite/character-package contracts, and the exact Phase 3 integration hooks.
- [ ] Run `dotnet test FgoPet.sln -c Release --no-restore`, `git diff --check`, and inspect `git status --short`.
- [ ] Leave formal user acceptance pending; mark implementation complete and ready for the user's Release test.
