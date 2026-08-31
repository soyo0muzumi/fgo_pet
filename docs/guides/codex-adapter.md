# FGO Pet Codex adapter

The adapter is an explicit, local bridge between Codex and the FGO Pet Agent
Relay. It uses the existing current-user-only named-pipe boundary. Pairing is
approved in FGO Pet; the plugin never receives or stores a Relay credential in
Codex configuration, command arguments, prompts, or environment variables.

## Package layout

The repository package is `integrations/codex/fgo-pet-agent`:

- `.codex-plugin/plugin.json` declares the plugin and versioned MCP server.
- `.mcp.json` starts `fgo-pet-codex-adapter mcp`.
- `hooks/hooks.json` maps deterministic lifecycle facts to
  `fgo-pet-codex-adapter hook <kind>`.
- `.agents/plugins/marketplace.json` is an addable local marketplace source.

The adapter and Relay are installed side by side. The adapter resolves its
sibling `FgoPet.AgentRelay.exe`; it does not search `PATH` for a Relay.

## Install

Run the installer explicitly from a PowerShell prompt. With no
`-PublishedSource`, it publishes the Release adapter and Relay. A pre-published
directory can be supplied to make the operation reproducible and avoid a
second build:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-codex-adapter.ps1 `
  -InstallRoot "$env:LOCALAPPDATA\FgoPet\bin"

# A directory containing adapter/ and relay/ publish output, or one directory
# containing both executables:
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-codex-adapter.ps1 `
  -InstallRoot "$env:LOCALAPPDATA\FgoPet\bin" `
  -SkipBuild -PublishedSource "C:\path\to\fgo-pet-publish"
```

The installer is idempotent. It creates
`%LOCALAPPDATA%\FgoPet\bin\fgo-pet-codex-adapter.cmd`, copies the published
Release files, validates the local manifest, and runs a bounded MCP
`initialize`/`tools/list` smoke test. It adds that exact directory to the user
`PATH` unless `-SkipUserPath` is supplied. `-SkipUserPath` and
`-SkipPluginRegistration` are intended for isolated acceptance runs; they do
not change the production package.

When plugin registration is enabled, the installer adds the repository's local
marketplace and installs `fgo-pet-agent@fgo-pet-local` using the installed Codex
CLI. Restart Codex after the installer reports a PATH change so the new shim is
resolvable.

## Pair and allow a target

1. Open FGO Pet's Agent Connection settings and enable the connection.
2. Start Codex or the installed MCP command. A pending adapter request appears
   in the settings page.
3. Approve the request, run `Test connection`, and save the source's target
   allowlist. New grants are default-deny until a target is explicitly allowed.
4. Register a project directory locally on the adapter machine:

   ```powershell
   fgo-pet-codex-adapter target add "C:\work\my-project" "My project"
   fgo-pet-codex-adapter target list
   ```

   `target add` requires an existing absolute project directory and returns a
   stable opaque ID such as `project-0123...`. The directory is kept in the
   adapter's protected local target catalog; only the opaque project ID is sent
   through the Relay. In FGO Pet settings, add that project ID to the approved
   source's allowed targets. Dispatch requests must use the project ID, never a
   filesystem path.

Use `--read-only` with `target add` when the Codex target must not be modified.
The app's permission editor controls which registered IDs a source may use;
target registration alone does not authorize dispatch.

## Adapter modes

- `fgo-pet-codex-adapter --version` or `describe` prints bounded JSON metadata
  (`name`, `version`, and `protocol_version`) for offline compatibility checks.
- `fgo-pet-codex-adapter mcp` serves MCP JSON-lines on standard input/output.
- `fgo-pet-codex-adapter hook started|resumed|attention|failed|cancelled`
  emits one deterministic lifecycle event for the Codex hook host.
- `fgo-pet-codex-adapter target list|add ...` manages the local opaque target
  catalog.
- `worker` is the bounded background dispatch worker started by the app/MCP
  runtime; it is not a second Relay and should not be launched as a user task.

MCP initialization and tools listing remain available while pairing is pending.
Completion tools return a structured approval-required status until the user
approves the source. Completion and goal reports also require explicit user
confirmation; stopping, asking a question, or reaching a milestone is not
completion.

For isolated tests only, `FGO_PET_PIPE_SUFFIX` and `FGO_PET_STATE_ROOT` may
override the pipe suffix and state root. Use a unique temporary value for each
run. These variables are not a credential channel and must never contain a
Relay credential.

## Restart, revoke, and uninstall

The adapter identity and Relay grants survive normal adapter/Relay restarts.
Revocation in Agent Connection settings takes effect immediately, cancels active
dispatches, and remains effective after restart. Pairing state is preserved by
the uninstaller:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-codex-adapter.ps1 `
  -InstallRoot "$env:LOCALAPPDATA\FgoPet\bin"
```

The uninstaller removes only files recorded by the installer marker, its user
PATH entry (when the installer added it), the owned plugin registration, and
the owned marketplace source. It does not remove Relay pairing or adapter
identity state unless explicitly requested:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-codex-adapter.ps1 `
  -InstallRoot "$env:LOCALAPPDATA\FgoPet\bin" -RemoveState
```

`-RemoveState` removes only the `AgentRelay` and `CodexAdapter` state
directories under the supplied/default FGO Pet state root. It does not remove
other FGO Pet application data.

## Validate the package

Validate the plugin before registration with the Codex plugin-creator
validator:

```powershell
python C:\Users\<user>\.codex\skills\.system\plugin-creator\scripts\validate_plugin.py `
  integrations\codex\fgo-pet-agent
```

The final Phase 4 script performs this validation, an isolated install, and the
MCP smoke path. Neither that smoke path nor simulated JSON-lines input is a
claim that a real Codex task executed; visible App/Codex dispatch evidence is a
separate acceptance step.

If the adapter reports `approval_required`, approve the pending request in FGO
Pet and retry. `RelayOffline`, `VersionMismatch`, and `AuthenticationFailed`
are distinct runtime states; do not work around them by passing a credential or
filesystem path through Codex.
