# ui-foundation Agent Rules

## Scope

This file applies to this directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own reusable WPF tokens, themes, colors, typography, spacing, controls, icons, converters, animations, and accessibility rules.

## Boundaries and dependencies

- Allowed dependencies: WPF/framework libraries and business-neutral `foundation` primitives.
- Forbidden dependencies: `desktop-shell` and every feature module, including their state and implementations.
- Cross-module consumers must use visual resources and controls without business semantics.

## Safety invariants

Resources must not embed secrets, prompts, user data, or business state. Preserve accessible contrast, keyboard behavior, safe resource loading, and deterministic theme selection.

## Minimum validation

Run affected theme/resource/common-control and Windows tests. Shared visual boundary changes require a release build and relevant integration coverage.
