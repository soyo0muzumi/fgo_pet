# desktop-shell Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own application startup, lifetime, tray, windows, attached-panel hosting, navigation, portrait hosting, shell-only presentation, and the composition root.

## Boundaries and dependencies

- Allowed dependencies: all business modules, `ui-foundation`, and `foundation`.
- Forbidden dependencies: feature modules must not depend on `desktop-shell`; shell code must not reach into another module's database or internal classes.
- Cross-module calls must use published contracts and composition-root wiring.

## Safety invariants

Keep offline shell operation available when Agent integration is unavailable. Preserve single-instance, window-placement, navigation, and startup recovery behavior. Do not execute content from role packages.

## Minimum validation

Documentation changes require Markdown and diff checks. Shell logic requires affected App and Windows tests. Startup, window, or cross-module changes also require the relevant end-to-end or integration checks and a release build.
