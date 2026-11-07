# Web Search Plugin

`CaptureAuthorization(scope)` returns a host-only revocation callback for the exact enabled provider/grant revision and current scope, without network I/O. It becomes false on settings changes, scope loss, unavailable runtime or plugin stop. Consumers need their own explicit feature permission in addition to this Web grant; the callback grants no arbitrary local writes. The opt-in Dialogue sticker collector reuses it for bounded public image downloads. Search providers, search-worker protocol and full-page reading permissions are unchanged.

`WebSearchPlugin` contributes the read-only `web.search` tool to the Native runtime. It invokes the bundled Python helper with one bounded JSON request on stdin. The helper uses the pinned DDGS DuckDuckGo engine directly, without an API key, metasearch fallback, MCP service, shell command, or runtime package installation. Full-page reading remains the separately authorized Browser capability.

General research does not require a browser connection. The tool description guides focused follow-up searches for missing facts, candidate names and relevant source domains, then comparison of supported findings. A returned link or snippet does not attest that the destination page was read or that current availability was verified.

## Host and configuration

The host supplies the current grant, current role/conversation/project scope, the existing `IBoundedProcessRunner`, and the absolute path `WebSearch/FgoPet.WebSearch.exe` beneath the application directory. The public constructor is:

```csharp
WebSearchPlugin(
    Func<WebSearchSettings?> settings,
    Func<ToolScope, bool> scopeCurrent,
    IBoundedProcessRunner runner,
    string workerPath,
    Action<WebSearchDiagnostic>? diagnostics = null,
    Func<string?>? configurationError = null)
```

`WebSearchSettingsStore` owns the local `web-search.json` grant. Reading settings or asking for availability does not create files, install dependencies, or access the network. An explicit `SetEnabledAsync` creates or rotates a grant containing `Enabled`, `AuthorizationId`, `AuthorizationVersion`, and `Provider=duckduckgo`. Disabled settings are retained for the UI but never authorize execution. Malformed JSON can be repaired by an explicit settings action; an unreadable file is not overwritten.

The old three-field Tavily grant returns `WEB_SEARCH_PROVIDER_CHANGED` until the user explicitly enables the new provider. Existing credentials are not read, modified, or deleted. Provider changes cannot silently reuse consent for another external service.

`GetSnapshot()` exposes `Configured`, `Enabled`, `Available`, and `ErrorCode`. Missing, disabled, invalid, unreadable, provider-changed, and missing-runtime states are distinct. The tool implements `IToolAvailabilityProvider` for the current model catalog, while execution always revalidates the grant and scope. Availability means the local prerequisites are ready, not that a network connection has already been tested.

`CheckConnectionAsync` is an explicit settings-page operation. It requires an enabled grant and searches only the fixed public query `DuckDuckGo`, with one result. It returns a safe availability/error result, never source text or conversation data.

## Execution and results

Model arguments remain `query` (1–400 Unicode scalars, nonblank, no control characters) and optional `maxResults` (1–5, default 5). Extra or duplicate arguments are rejected before process creation. Model arguments cannot choose a provider, executable, script, endpoint, environment, or working directory.

The Windows bounded process runner uses one process, bounded memory/output, and the plugin's 45-second total deadline. The query is sent through stdin rather than command-line arguments. Cancellation and plugin shutdown cancel the process; the grant/scope is checked before launch, at the runner's start boundary, after completion, and before releasing results. Revocation discards results. No automatic retry occurs.

The Python helper accepts protocol version 1. It uses `ddgs==9.16.1` and only the DuckDuckGo HTML engine with TLS verification and redirects disabled. HTTP rejection, rate-limit/challenge pages, timeouts, malformed responses, and recognized empty results are distinct outcomes. It never visits returned URLs. See [the integration](../../tools/integrations/web-search/README.md) for dependency and bundle details.

Results retain URL, title, snippet, capture time, and SHA-256 response snapshot hash. `coverage=search_snippets`, `exhaustive=false`, `sourceVersionKind=search_snapshot_sha256`, `returnedResults`, and clipping flags retain their meanings. Titles and snippets are capped at 256 and 2,000 Unicode scalars. Unsafe URLs are omitted. Helper output is capped at 128 KiB and the model payload at 48 KiB; omitted or clipped sources are disclosed. Search data cannot grant permissions or turn itself into confirmed memory.

Failures return allowlisted error codes, never stderr or exception text. Diagnostics contain only timestamp, hashed correlation, stage, and safe error code; they do not contain queries, URLs, source bodies, or local paths.

## Build and verification

The test package must include the complete helper directory, including its Python runtime, native dependencies, licenses, and `runtime-manifest.json`. `FgoPetWebSearchRuntime` points MSBuild at the built directory. A local `artifacts/web-search/runtime` is detected for development; otherwise the UI reports `WEB_SEARCH_RUNTIME_MISSING`. Publishing requires a prepared bundle. Testers do not install Python or pip.

```powershell
pwsh -File tools/integrations/web-search/build.ps1

dotnet build plugins/FgoPet.Plugin.WebSearch/FgoPet.Plugin.WebSearch.csproj -c Release -warnaserror
dotnet test tests/FgoPet.Capability.Tests/FgoPet.Capability.Tests.csproj -c Release --filter FullyQualifiedName~WebSearch
python -m pytest tests/web_search
```

Unit tests use synthetic bounded process results and temporary settings. Windows boundary tests pass both search entry points through the real bounded process runner with an invalid executable fixture, verifying request validation and safe launch-failure handling without network access. The bundled executable's `--self-test` checks its protocol, pinned dependency, and engine imports without network access. Network reachability and successful helper startup are separate checks and must not be inferred from these tests.
