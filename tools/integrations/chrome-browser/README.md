# Chrome browsing for FGO Pet

This integration uses Google's [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) (Apache-2.0), pinned to **1.10.1**. Node 20.19+, 22.12+, or 23+ and Chrome 144+ are upstream prerequisites. FGO Pet's browser plugin connects to the existing Chrome profile with `--autoConnect`, preserving normal website login state. No cookies or passwords are copied into FGO Pet configuration.

Setup is a local installation and permission change. Run only after the user authorizes those operations:

```powershell
pwsh -File tools/integrations/chrome-browser/setup.ps1 -Enable
```

The script installs into the ignored `artifacts/chrome-browser/runtime` directory, with lifecycle scripts and optional dependencies disabled. It writes a new `chrome-browser.json` in `FGO_PET_STATE_ROOT`, or `%LOCALAPPDATA%/FgoPet` by default, and refuses to overwrite an existing grant. Move no existing user data. For dependency installation without activation, omit `-Enable`.

The configuration contains `Enabled`, absolute `NodePath`, absolute `ServerPath`, and optionally an absolute `UserDataDirectory` for a portable/custom Chrome profile. For a new grant, supply `-UserDataDirectory <existing-profile-directory>` to setup. This uses the official connector's profile discovery and still requires Chrome approval; it does not launch Chrome or copy profile data. It is a host permission file, not a model-editable setting or tool argument. `Enabled: true` permits the active character's current conversations to browse the connected Chrome profile. Page data read by a tool is returned to the conversation's configured model. Disable `Enabled` to deny subsequent reads and discard results of in-flight calls after their next boundary. Cached snapshots cannot be accessed while disabled.

In Chrome, open `chrome://inspect/#remote-debugging` and enable remote debugging. Chrome asks whether to allow the incoming connection when the first browser tool runs; the user must make that choice. Keep Chrome running. The server can connect to the profile selected by Chrome, including its existing tabs. See the [upstream connection guide](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/advanced-usage.md#automatically-connecting-to-a-running-chrome-instance).

Use a build that contains `FgoPet.Plugin.Browser`, then ask the character to open and read a public page. The expected chain is Native model tool call → browser plugin → official MCP → Chrome → bounded source-bearing observation → the same Native model loop. A successful manual CLI call alone does not validate that chain, and synthetic model tests do not establish the quality of the character's understanding.

Initial tools only list web tabs, open HTTP(S) links in new tabs and read loaded accessibility snapshots. Search can use a site's search URL. Generic clicking, typing, form submission, uploads, arbitrary JavaScript and sending messages are unavailable. Unloaded/virtualized content and video may require additional capabilities later. There is no automatic background crawler or memory promotion.
