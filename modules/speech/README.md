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

## Public playback boundary

`FgoPet.Speech.Core/Contracts/IConfiguredSpeechPlayback.cs` exposes saved-configuration playback, stop, read-only state and state notifications. It does not expose raw synthesis, settings mutation, credentials, devices or disposal. Provider selection, text filtering, auto-read/DND rules, generation cancellation and audio cleanup stay in the existing `SpeechPlaybackCoordinator`.

`SpeechPlaybackResult` is compiled once by Speech.Core; its existing `FgoPet.App.Speech` namespace is retained for source compatibility. The namespace does not imply a reference to an application or Desktop assembly. The result type's assembly and the Dialogue consumer's constructor signature have changed: rebuild and deploy the whole application rather than replacing a single DLL.

State notifications may be raised on a worker thread. Each desktop consumer must marshal updates to its own UI dispatcher and invalidate queued work when its request or lifetime changes. The port does not create a UI dispatcher or claim to be a thread-affine event source.

## Unit-test projects

- `modules/speech/tests/FgoPet.Speech.Core.Tests/FgoPet.Speech.Core.Tests.csproj`
- `modules/speech/tests/FgoPet.Speech.Infrastructure.Tests/FgoPet.Speech.Infrastructure.Tests.csproj`
- `modules/speech/tests/FgoPet.Speech.Desktop.Tests/FgoPet.Speech.Desktop.Tests.csproj`

## Dependencies and security

The module owns `SpeechSettings` and `ISpeechSettingsStore`, and consumes the protected credential contract and implementation. It must not depend on Dialogue, Todo, Focus, Agent transport, or desktop-shell implementation. OpenAI-compatible keys remain in Windows Credential Manager; GPT-SoVITS accepts only loopback HTTP. Speech text, credentials, raw audio, reference-audio paths, and local paths are not sent through Agent or written to diagnostics.

## Composition

`FgoPet.App` references `FgoPet.Speech.Desktop` and remains the composition root. `host/Settings` implements the speech-owned settings port as one section of the schema-v2 document; no second settings or dialogue shell is introduced.

The host's existing explicit factory passes the same `SpeechPlaybackCoordinator` singleton to Dialogue through `IConfiguredSpeechPlayback`. Settings preview retains its existing implementation-level use within the Speech surface. No second playback instance or alias with a separate lifetime is created. The host owns disposal of synthesis, playback and the audio player; disposing the Dialogue consumer only stops its current playback and detaches its own subscriptions.

## Validation

Run all three speech test projects plus the affected solution tests and Release build. Project/reference changes also require Phase 4; the workflow includes speech source and Dialogue project-file paths without changing its three-round or failure criteria.

Core-only tests protect contract availability and the absence of raw-synthesis/disposal methods. Desktop tests call the real coordinator through the port and retain auto-read/DND, manual playback and late-audio cancellation coverage. Dialogue tests exercise dispatcher delivery, stale notifications/results, context changes, disposal and continued text chat. The architecture gate checks both evaluated project references and the actual Dialogue AssemblyRef table.

Real provider playback, sound quality/latency, DPI, IME, multi-monitor, Narrator, and high-contrast checks remain manual acceptance boundaries. Contract doubles are not device or provider acceptance.

## Migration status

The v0.3 speech contracts, adapters, playback lifecycle, settings integration, and speech tests live under this module. Schema-v2 composition lives only in `host/Settings`; platform persistence remains unaware of speech types. Dialogue consumes Speech.Core rather than Speech.Desktop; this does not claim all other Dialogue or host dependencies have been isolated.
