# dialogue Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own sessions, providers, prompt composition, knowledge binding, structured output, dialogue tools, and dialogue presentation.

## Boundaries and dependencies

- Allowed dependencies: `memory` published read-only contracts, `servant-packs` contracts, `ui-foundation`, and `foundation`.
- Forbidden dependencies: memory implementation, memory review/deletion controls, `desktop-shell`, Relay, Codex Adapter, and direct database tables.
- Cross-module calls must use explicit published interfaces.

## Safety invariants

Never expose credentials, complete private prompts, tool arguments, terminal output, or unnecessary local paths in payloads or logs. Reject unsafe structured output and preserve approval boundaries.

## Minimum validation

Run affected dialogue unit tests, provider tests, and Windows presentation tests. Provider, persistence, or cross-module changes require the relevant integration/end-to-end checks and release build.
