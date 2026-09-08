# todo Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own Todo data, state, priority, repository, UI, proposal confirmation, timeline display, and the Todo-side dispatch adapter.

## Boundaries and dependencies

- Allowed dependencies: `agent-integration` narrow published dispatch contract, `ui-foundation`, and `foundation`.
- Forbidden dependencies: Relay, Codex Adapter, pipeline implementation, `desktop-shell`, and other modules' tables or internals.
- Cross-module calls must use explicit contracts and user-confirmed requests.

## Safety invariants

Never dispatch a proposal without explicit confirmation. Validate Todo state transitions and target identifiers. Preserve default-deny Agent authorization and do not log private task content or tool arguments.

## Minimum validation

Run Todo Core, App, Infrastructure, and Windows tests. Dispatch or persistence changes also require relevant Agent integration/end-to-end checks and the release build.
