# Browser Plugin

`ChromeBrowserPlugin` contributes `browser.tabs`, `browser.open`, and `browser.read` to the existing Native tool registry. It connects on demand to a locally installed, pinned Chrome DevTools MCP **1.10.1**, using Node and stdio. It does not install software, launch another browser, change Chrome settings, replace the model runtime, or introduce an agent scheduler.

The host loads an explicit `chrome-browser.json` grant from its state directory. Missing, invalid or disabled configuration denies access. The active-role/current-conversation check is repeated around protocol calls and before results are returned. Chrome's own remote-debugging connection approval remains required. This grant covers browsing in the connected profile for active conversations; it is not a per-site grant. Disable `Enabled` or disconnect in Chrome to withdraw access; an in-flight protocol call is bounded to 45 seconds, and changed authorization discards its result. Ordinary chat and offline capabilities remain available without Chrome.

The facade only calls `list_pages`, `new_page`, and `take_snapshot`. It rejects extra model arguments, credential-bearing URLs and non-HTTP(S) schemes. Opening a link creates a background tab and does not replace existing tabs. This is browsing access, including normal website loading effects, not a guarantee that arbitrary websites implement side-effect-free GET requests. No generic click, forms, uploads, downloads, script evaluation, filesystem output, network-header inspection or DevTools tools are exposed. The server's telemetry, CrUX requests, JavaScript evaluation, file navigation and ambient configuration discovery are disabled. Existing page scripts still run normally in Chrome.

Page snapshots are untrusted tool observations. They retain URL, title, capture time, content hash and partial-read indicators. Text is chunked by Unicode scalar; cursors refer to an immutable in-memory snapshot scoped to the conversation, role, project and host grant. At most eight snapshots are retained, for at most five minutes of cursor access. A snapshot covers currently loaded accessibility content, not video/audio, the whole site, unloaded messages or long-term memory. Nothing here confirms a memory. Metadata diagnostics exclude URLs, titles, content, arguments and credentials.

`browser.read` defaults to, and is capped at, 8,000 Unicode scalars per call. Follow `nextCursor` for contiguous coverage; `offset`, `returnedCharacters`, `totalSnapshotCharacters` and `truncated` describe the actual portion returned. A successful call or final chat answer does not imply all snapshot segments were read. Native conversation runs reserve their last model request for a final answer with partial-coverage disclosure rather than offering another read.

Connection interruption closes only the owned MCP process and does not retry the failed request. Browse failures can leave a newly opened tab; inspect tabs before retrying. Protocol input, read sizes, process lifetime and output are bounded. Browser-consent prompts require the user; do not bypass them.

Build and test:

```powershell
dotnet build plugins/FgoPet.Plugin.Browser/FgoPet.Plugin.Browser.csproj -c Release -warnaserror
dotnet test tests/FgoPet.Capability.Tests/FgoPet.Capability.Tests.csproj -c Release --filter FullyQualifiedName~Browser
```

The local setup entry is `tools/integrations/chrome-browser/setup.ps1`; installation and host activation are explicit operations. See that integration's README. Synthetic protocol and Native-loop tests are not real-browser/model acceptance.
