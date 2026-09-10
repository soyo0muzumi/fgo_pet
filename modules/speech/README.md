# speech

## Responsibilities

Own provider-neutral speech synthesis contracts, OpenAI-compatible and GPT-SoVITS adapters, text filtering/splitting, playback lifecycle, cancellation, temporary audio cleanup, DND-gated auto-read, and speech connection settings.

## Non-responsibilities

Do not own conversation/session state, Todo or Focus state, Agent Relay/dispatch, credentials, or desktop-window lifecycle. The composition root wires speech into the existing DialogueWindow and PortraitWindow surfaces without creating another window or provider fallback path.

## Production projects

- `modules/speech/src/FgoPet.Speech.Core/FgoPet.Speech.Core.csproj`
- `modules/speech/src/FgoPet.Speech.Infrastructure/FgoPet.Speech.Infrastructure.csproj`
- `modules/speech/src/FgoPet.Speech.Desktop/FgoPet.Speech.Desktop.csproj`

The public namespaces remain `FgoPet.Core.Speech`, `FgoPet.Infrastructure.Speech`, and `FgoPet.App.Speech` / `FgoPet.App.Settings` so the first stable point does not require a consumer-wide rename.

## Unit-test projects

- `modules/speech/tests/FgoPet.Speech.Core.Tests/FgoPet.Speech.Core.Tests.csproj`
- `modules/speech/tests/FgoPet.Speech.Infrastructure.Tests/FgoPet.Speech.Infrastructure.Tests.csproj`
- `modules/speech/tests/FgoPet.Speech.Desktop.Tests/FgoPet.Speech.Desktop.Tests.csproj`

## Dependencies and security

The module may consume shared settings and credential contracts and the existing protected credential implementation. It must not depend on Dialogue, Todo, Focus, Agent transport, or desktop-shell implementation. OpenAI-compatible keys remain in Windows Credential Manager; GPT-SoVITS accepts only loopback HTTP. Speech text, credentials, raw audio, reference-audio paths, and local paths are not sent through Agent or written to diagnostics.

## Composition

`FgoPet.App` references `FgoPet.Speech.Desktop` and remains the only composition root. Speech settings are hosted by the existing singleton settings resolver and DialogueWindow; no second settings or dialogue shell is introduced.

## Validation

Run all three speech test projects plus the affected solution tests and Release build. Real provider playback, sound quality/latency, DPI, IME, multi-monitor, Narrator, and high-contrast checks remain manual acceptance boundaries.

## Migration status

The v0.3 speech contracts, adapters, playback lifecycle, settings integration, and speech tests now live under this module. Shared AppSettings persistence codecs remain in the shared Core/Infrastructure projects by design; they carry only non-secret settings and do not own speech behavior.