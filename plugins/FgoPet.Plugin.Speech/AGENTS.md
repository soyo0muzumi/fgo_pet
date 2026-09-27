# speech Module Rules

## Scope

This file applies to the `plugins/FgoPet.Plugin.Speech` tree and inherits the repository root `AGENTS.md`.

## Ownership

Own provider-neutral speech contracts, the OpenAI-compatible and GPT-SoVITS adapters, text filtering, playback lifecycle, speech settings integration, cancellation, and temporary-audio cleanup.

## Boundaries

The module consumes published Platform settings/credential contracts; the Windows Credential Manager implementation is in `src/FgoPet.Platform/Windows` and the application composition root owns cross-module wiring and window navigation. Speech projects must not reference unrelated infrastructure or Agent implementation. Speech must not depend on Dialogue, Todo, Focus, Agent transport, or Desktop shell implementation.

## Safety invariants

OpenAI-compatible keys stay in Windows Credential Manager. GPT-SoVITS is loopback-only and never installs models or starts a service. Do not log speech text, credentials, raw audio, reference-audio paths, or local paths through Agent or application diagnostics. A provider failure must not silently fall back to another provider.

Playback cancellation and generation checks must prevent late audio from playing. Temporary WAV files are session-scoped and cleaned up on success, cancellation, and failure.

## Minimum validation

Run all three speech unit-test projects, the affected App and Windows tests, the Release build, and the Phase 4 gate when project or packaging references change. Real provider playback and device accessibility remain explicit manual acceptance boundaries.
