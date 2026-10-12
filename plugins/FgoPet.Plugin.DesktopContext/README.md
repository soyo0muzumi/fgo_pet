# Desktop Context Plugin

Small, on-demand read-only observations for the Native tool loop. It depends only on Extensibility; it owns no conversation history, memory, tasks, background polling or model connection.

`context.time` samples the host clock once and returns UTC sampling time, local date/time, time zone ID, offset and daylight-saving status. The host validates the current conversation/role/project around the observation. Old conversation timestamps are not a substitute for a current sample. The production host registers this tool.

`context.apps` is **not registered by the production host**. It is available only when a trusted caller supplies both a revocable scope authorization callback and an application reader. Model arguments cannot supply or enable either. No persistent grant or settings entry is created. Revocation during observation discards the result.

`WindowsApplicationDiscovery` can enumerate visible, non-cloaked top-level windows in the current desktop and group them by executable stem. It excludes the desktop shell, bounds enumeration to 512 windows and checks a two-second scan deadline, and returns at most 32 applications. Results carry start/end sampling times, partial-coverage status and whether the sampled foreground window was represented. Protected, disappearing or unsupported windows may be unavailable; process existence alone does not count as an open application. The deadline is checked between synchronous Windows queries, not a hard process timeout.

No window titles, command lines, executable paths, text bodies, screenshots, keystrokes or browser profile data are collected by this adapter. Discovery does not establish that an application's content can be read. No second application's content reader is included. Names are untrusted observation data and may not be user-facing application names.

Diagnostics contain only timestamp, hashed invocation correlation, phase and safe error code. No application observations or tool arguments are logged.

Validation: run `FgoPet.Capability.Tests` filtered to `DesktopContext` and `FgoPet.App.Tests` filtered to `DesktopContextWindowTests|NativeConversationIntegrationTests|ServiceRegistrationTests`. Fixtures cover bounded observations and revocation; the Win32 check queries only the test's own hidden window handle. Synthetic Native model tests and that controlled handle check do not establish real desktop-model acceptance or full desktop discovery coverage.
