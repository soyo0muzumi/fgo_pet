# Project workflow learnings

## [LRN-20260831-001] correction

**Logged**: 2026-08-31
**Priority**: high
**Status**: in_progress
**Area**: config

### Summary
User prefers a lightweight, risk-based workflow for this small-to-medium project, not mandatory per-task Superpowers implementation/review loops.

### Details
Repeated full-suite runs, agent handoffs, and process artifacts lengthened Phase 4 repair excessively. Model caution is helpful but is not execution evidence; retain verification at actual integration and security boundaries.

### Suggested Action
Batch adjacent implementation work. Run focused tests only for changed code and affected dependencies; reuse results for unchanged code/environment. Do not spawn a reviewer or write a new plan/report for each small change. Reserve independent review for authentication, durable authorization, and concurrency risks. Run one final full regression plus the agreed real-machine acceptance. Keep confirmed defects open until fixed and verified. This user-directed workflow overrides the previous blanket per-task review/full-suite cadence, but not safety or permission boundaries.

### Metadata
- Source: user_feedback
- Related Files: .superpowers/sdd/2026-08-31-phase4-runtime-repair-implementation/progress.md
- Pattern-Key: workflow.risk_proportionate_verification
- Tags: workflow, superpowers, testing

