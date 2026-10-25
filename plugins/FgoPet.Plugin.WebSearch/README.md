# Web Search Plugin

`WebSearchPlugin` contributes the read-only `web.search` tool to the existing Native runtime. Tavily's official Search API supplies search, ranking and source snippets; this plugin only validates input, enforces the host grant, makes one bounded HTTP request and projects bounded source data. It introduces no search engine, MCP client platform, CLI, agent scheduler or third-party package.

The plugin targets .NET 8 and references only the existing Extensibility contracts. Its production HTTP client disables redirects and cookies and uses the fixed `https://api.tavily.com/search` endpoint. Model arguments are limited to `query` (1–400 Unicode scalars, nonblank and without control characters) and optional `maxResults` (1–5, default 5). Extra or duplicate arguments are rejected before credentials or HTTP access.

The host fixes `topic=general`, `search_depth=basic`, `auto_parameters=false`, `include_answer=false`, `include_raw_content=false`, `include_images=false`, `chunks_per_source=1` and `include_usage=true`. Response limits are enforced locally regardless of upstream chunk behavior. The adapter does not retry, switch providers, enter keyless mode, upgrade a plan or start authentication.

## Host ownership and authorization

The host supplies three delegates: the current `WebSearchSettings`, the active-role/current-conversation/project scope check, and a protected key reader. The public constructor is:

```csharp
WebSearchPlugin(
    Func<WebSearchSettings?> settings,
    Func<ToolScope, bool> scopeCurrent,
    Func<CancellationToken, Task<string?>> readProtectedKey,
    Action<WebSearchDiagnostic>? diagnostics = null)
```

`WebSearchSettings.Load(path)` reads at most 4 KiB and accepts exactly `Enabled`, `AuthorizationId` and `AuthorizationVersion`. The identifier is a nonzero GUID in `N` format; the revision is positive. Missing, invalid or disabled settings deny access. No credential, custom endpoint or other configuration property is accepted. `WebSearchSettings.CredentialTarget` is `FgoPet:WebSearch:Tavily`; the App host owns Windows Credential Manager access. The plugin does not read ambient environment variables or write settings or credentials.

Use a fresh authorization identifier for a new grant and advance the revision when reauthorizing or changing its scope. Before credential changes or revocation, withdraw or advance the grant so in-flight results cannot outlive that authorization. A configured tool is still denied without a valid host grant, current scope and protected credential. Registration and skill loading do not authorize a call.

The adapter checks the grant and scope before reading the key, again before dispatch, after HTTP and body reads, and before returning results. A changed grant or scope discards the response; a failed host authorization read safely denies access. Each serialized operation has a 45-second total deadline including queued time and credential access. Caller cancellation and plugin shutdown cancel work; no request is replayed. Synchronous and asynchronous disposal share idempotent termination and owned HTTP-client cleanup; synchronous disposal does not wait for a request to finish.

## Data, coverage and diagnostics

Queries and fixed search parameters go to Tavily only when the host authorizes the call. Result snippets enter the existing conversation/model loop. Browser reading has its own authorization and website/model data boundary; this plugin never opens a result URL or reads browser state. Account creation, credentials, service use and any costs are separate host/user operations.

HTTP bodies are capped at 128 KiB with or without Content-Length. Only JSON without content encoding is accepted. Source URLs must be bounded HTTP(S) URLs with no user information or control characters. Invalid URLs are omitted. Titles and snippets must contain valid Unicode; only line breaks and tabs are allowed as controls in snippets. Returned titles are capped at 256 Unicode scalars and snippets at 2,000, with per-field truncation flags. The current protected credential cannot appear in returned source fields.

Results carry URL, title, snippet, capture time and a SHA-256 search-response snapshot hash. `sourceVersionKind=search_snapshot_sha256` names the hash's meaning: it is not an origin webpage version. `coverage=search_snippets` and `exhaustive=false` disclose the actual coverage. `returnedResults` counts only the returned array; `truncated` marks omitted or clipped response data. The actual serialized UTF-8 payload is capped at 48 KiB; lower-ranked entries are omitted with disclosure before reaching the Native host's 64-KiB payload cap. No total web result count, search cursor, full-page claim or confirmed memory is fabricated. Empty results remain distinct from denial, unavailable credentials, HTTP failure and malformed responses. Untrusted snippets never provide instructions or authority.

HTTP authentication, forbidden, rate-limit, quota, payment-limit, redirect and service errors have separate safe codes. Upstream error bodies, exception messages and credentials are never returned or logged. Diagnostics contain only timestamp, a hashed correlation, stage and safe error code; they exclude query, URL, arguments, content, key and request/response bodies. Diagnostic callbacks are isolated from operation outcomes.

## Verification

```powershell
dotnet build plugins/FgoPet.Plugin.WebSearch/FgoPet.Plugin.WebSearch.csproj -c Release -warnaserror
dotnet test tests/FgoPet.Capability.Tests/FgoPet.Capability.Tests.csproj -c Release --filter FullyQualifiedName~WebSearch
```

Tests use the production adapter with synthetic `HttpMessageHandler` responses and temporary synthetic settings. They do not access Tavily, protected credentials, production settings or the application. Real service connectivity, account limits, language/search quality and browser/model acceptance require separate authorization and validation.

Contract source: [Tavily Search API](https://docs.tavily.com/documentation/api-reference/endpoint/search). Service prices and terms are owned by Tavily and should be checked before authorized use; the MIT license of Tavily's MCP/skills does not license service access. The selection referenced official [MCP source at 1c1d54c](https://github.com/tavily-ai/tavily-mcp/tree/1c1d54c2a619afe52544f775c8d82a56ac6d8bb5) and [search skill at 778122e](https://github.com/tavily-ai/skills/blob/778122e5f9c680f541eeceda5a5b36405eb7980c/skills/tavily-search/SKILL.md); neither upstream runtime nor executable skill is bundled here.
