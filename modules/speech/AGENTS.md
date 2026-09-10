# speech Module Rules

## Scope

This file applies to the `modules/speech` tree and inherits the repository root `AGENTS.md`.

## Ownership

Own provider-neutral speech contracts, the OpenAI-compatible and GPT-SoVITS adapters, text filtering, playback lifecycle, speech settings integration, cancellation, and temporary-audio cleanup.

## Boundaries

The module may consume shared Core settings and credential contracts. The Windows Credential Manager implementation is supplied by the application composition root; speech production projects must not reference the full Infrastructure or Agent assemblies. It must not depend on Dialogue, Todo, Focus, Agent transport, or desktop-shell implementation. The application composition root owns cross-module wiring and window navigation.

## Safety invariants

OpenAI-compatible keys stay in Windows Credential Manager. GPT-SoVITS is loopback-only and never installs models or starts a service. Do not log speech text, credentials, raw audio, reference-audio paths, or local paths through Agent or application diagnostics. A provider failure must not silently fall back to another provider.

Playback cancellation and generation checks must prevent late audio from playing. Temporary WAV files are session-scoped and cleaned up on success, cancellation, and failure.

## Minimum validation

Run all three speech unit-test projects, the affected App and Windows tests, the Release build, and the Phase 4 gate when project or packaging references change. Real provider playback and device accessibility remain explicit manual acceptance boundaries.