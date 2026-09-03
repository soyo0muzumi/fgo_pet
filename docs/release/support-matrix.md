# First release candidate support matrix

## Supported contract

| Area | Contract |
| --- | --- |
| Platform | Windows x64 only, within the selected verified environment. This candidate makes no general Windows 10 or Windows 11 compatibility claim. |
| Application runtime | Framework-dependent `net8.0-windows` App publish. Install the .NET 8 Desktop Runtime before starting the App. |
| Startup without services | After a role package is installed and active, the App and focus features start offline without a configured AI model or Agent connection. |
| Agent components | Relay, Adapter, and Codex integration are optional. They do not prevent offline App startup when absent or disabled. |
| Role resources | The App payload and `.fgopetpack` role packages are separate artifacts and output roots. A role package must be installed and activated before the pet desktop experience is available. |
| Uninstall data handling | Uninstall preserves user data by default. Removing data is a separate, explicitly confirmed action. |

## Publish profile contract

`win-x64-release` is a Release, framework-dependent publish for `win-x64`. It
does not bundle the App into a single file or extract native libraries at
runtime. The existing `CopySiblingRelay` and `PublishSiblingRelay` targets are
the authoritative mechanism for keeping the Relay and Adapter companion
binaries as separate files in the App payload.

## Unsupported for this first candidate

- Non-Windows and non-x64 environments.
- Self-contained, single-file, or native-library self-extract distribution.
- Treating a `.fgopetpack` as embedded App payload or distributing role
  resources from the App output root.
- Requiring an AI model, network connection, or Agent components for offline
  App startup after role-package activation.
- Automatic update, cloud synchronization, mobile distribution, or a second
  Agent platform.
- A command-line, PowerShell, or manual `PATH` workflow for end users to
  install, configure, repair, or remove Agent components.
- Uninstall flows that remove user data without a separate explicit
  confirmation.
- Public-release authorization or broad Windows-version support claims before
  the selected environment's installation, upgrade, rollback, uninstall,
  DPI, multi-monitor, sleep/resume, and long-running evidence is recorded.
