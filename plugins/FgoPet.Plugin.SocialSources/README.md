# Social Sources Plugin

`SocialSourcesPlugin` contributes one read-only tool per supported social platform to the Native runtime:

| Tool | Platform | Capability |
| --- | --- | --- |
| `social.bilibili` | B站 | keyword video search; single video detail |
| `social.xiaohongshu` | 小红书 | single public note detail (no keyword search) |
| `social.xiaoheihe` | 小黑盒 | keyword post search; single post detail |

Every tool returns third-party user-generated content. Results carry `capturedAtUtc`, `sourceVersion`
(a SHA-256 of the response snapshot), `sourceVersionKind`, `coverage`, `exhaustive` and `truncated` so a
finding can be attributed to the exact response it came from. `exhaustive` is always `false`: a page of
results is never the whole platform.

## Grants

Each platform owns a separate grant file in the storage root: `social-bilibili.json`,
`social-xiaohongshu.json`, `social-xiaoheihe.json`. A file records `Enabled`, `AuthorizationId`,
`AuthorizationVersion` and `Platform`. Keeping the platform in the file means a grant written for one
service can never be read as consent for another; a mismatched file is reported as
`SOCIAL_CONFIG_INVALID` rather than silently accepted.

All three platforms start disabled. Reading, asking for availability, or a missing file never creates
state, repairs configuration or touches the network. Only an explicit `SetEnabledAsync` writes a grant,
and every write rotates `AuthorizationId` and increments `AuthorizationVersion`. Disabling a platform
keeps the file for inspection but never authorizes execution.

There is no settings page for these grants. Enable a platform by calling
`SocialSourceSettingsStore.SetEnabledAsync(true, token)`, or by writing the file yourself with a fresh
`Guid("N")` identifier and a positive version.

## Execution and results

Model arguments are `query` (1–200 characters, search) **or** `reference` (a platform URL or id, single
item) — exactly one of them — plus optional `maxResults` (1–10, search only). Extra fields, duplicate
keys, both modes at once, or `maxResults` on a read are rejected before any request is made. Model
arguments can never choose a platform, endpoint, credential or working directory.

Every outbound request goes through `SocialHttpGateway`: redirects off, cookies off, automatic
decompression off, 20-second total budget, 512 KiB response cap. A non-OK status, an authentication
refusal, a rate-limit signal, a captcha/refusal envelope and malformed JSON each map to a distinct
allowlisted error code; none are reported as an empty result. There is no retry, no crawler and no
background polling.

Results are sanitized before they reach the model: URLs must be absolute http(s) with a host and no
embedded credentials, titles are clipped to 256 and summaries to 2000 Unicode scalars (with truncation
disclosed), control characters are rejected, and platform-injected `<em>` highlighting is stripped.
Payloads are capped at 48 KiB. Content cannot grant permission, alter settings or become confirmed
memory. Diagnostics carry only timestamp, hashed correlation, stage and a safe error code — never a
query, URL, page body, credential or local path.

## Platform notes

- **B站** — disabled by default and must be enabled by an explicit grant. Reads public endpoints the
  platform serves to its own web client. Anonymous access needs the device cookie pair from
  `x/frontend/finger/spi`; the request signature (`wbi`) is computed locally and never involves an
  account. See the operator note below before enabling.
- **小红书** — the platform refuses anonymous keyword search (`search/notes` returns
  `无登录信息` before it even validates the signature, so a correct signature does not help). `query`
  therefore discovers note links through a public search index restricted to
  `site:xiaohongshu.com/explore`, and `reference` reads one note from the server-rendered page state.
- **小黑盒** — the `hkey` request signature and the fixed web-client query parameters are computed
  locally; the construction is reproduced from the MIT-licensed `Insect-rippler/XIAOHEIHE_MCP` project
  (see `THIRD-PARTY-NOTICES.md`). The platform rejects the older digits-only `hkey` with `非法请求`, and
  it answers the older mobile-client parameter set with `status: ok` and an empty `items` list, so both
  the signature and the parameter set are protocol, not preference. `reference` reads one post via
  `link/tree`; without a session that endpoint answers `show_captcha` (`SOCIAL_CAPTCHA_REQUIRED`), which
  is why post reading and keyword search both effectively need the optional session. Results of
  `general/search/v1` mix posts, topics, mini apps and spacer rows; only `type: "link"` rows are returned.

### Discovery mode

`query` on 小红书 and 小黑盒 does **not** call a platform search API — it sends the query to a public
search index and keeps only results whose URL matches the platform's note/post URL shape. Returns carry
`coverage=search_index_discovery` and `sourceVersionKind=discovery_url_set_sha256`, and the rows contain
the index title and snippet only. Read any result with `reference` to obtain the platform's own fields.
This is a discovery step, not proof that the platform was searched, and the index may have stale or
missing entries — particularly on 小黑盒, where index coverage of individual posts is thin.

### Operator note for B站

The upstream `Nemo2011/bilibili-api` project was permanently shut down in July 2026 after the platform
operator sent a legal notice objecting to scraping capability being bundled into distributed software.
Bilibili's Open Platform does not expose a content search API and does not admit individual developers.
The `social.bilibili` tool therefore ships **disabled**: enabling it is a deliberate operator decision,
made in the knowledge that the platform's terms and its robots directives do not authorize this kind of
automated read. Keep it off in any build that is distributed to third parties unless that decision has
been made explicitly.

## Optional authenticated session

Three extra tools exist **only** when the host passes session storage and a credential source:

| Tool | Purpose |
| --- | --- |
| `social.bilibili.search` | platform search with the user's own signed-in session |
| `social.xiaohongshu.search` | platform search with the user's own signed-in session |
| `social.xiaoheihe.search` | platform search with the user's own signed-in session |

They are separate from the anonymous readers in every way that matters:

- **Separate grants.** `social-session-xiaohongshu.json` / `social-session-xiaoheihe.json`, each with its
  own `AuthorizationId`, `AuthorizationVersion` and `Platform`. Enabling an anonymous read grant never
  enables a session search, and vice versa.
- **Separate secret storage.** The session lives only in Windows Credential Manager under
  `FgoPet.SocialSources.<platform>`. It is never written to a settings file, a model payload, a
  diagnostic or a log. A copied settings file therefore grants nothing.
- **Separate connection.** `ISocialSessionConnection` is a single-flight object that resolves the secret
  lazily on first use, caches it only in memory, and drops it on reset. `ReconnectSessionAsync` forces a
  fresh read; disabling the grant resets the connection first.
- **Default off.** Absent, disabled, or secret-less sessions report `SOCIAL_SESSION_NOT_CONFIGURED` /
  `SOCIAL_SESSION_MISSING` and send no request.

Host operations (never reachable from a model argument):

```csharp
plugin.GetSessionSnapshot(platform);                        // local state, reads no secret, no network
await plugin.SetSessionEnabledAsync(platform, true, token); // explicit grant, rotates the identifier
await plugin.CheckSessionAsync(platform, token);            // one fixed neutral query, returns no source text
await plugin.ReconnectSessionAsync(platform);               // discard the cached secret
```

Session failure codes are distinct and never retried: `SOCIAL_SESSION_MISSING` (no stored secret),
`SOCIAL_SESSION_EXPIRED` (platform rejected it), `SOCIAL_SESSION_INVALID`, `SOCIAL_SESSION_CHANGED`
(grant or scope changed mid-call, result discarded), `SOCIAL_SESSION_UNAVAILABLE` (secret store failed),
`SOCIAL_SESSION_SIGNATURE_REQUIRED` (小红书 refused an unsigned request while accepting the same session
when the web client's signature is present).

To store a session for a personal deployment, write a generic credential with that target name, for
example `cmdkey /generic:FgoPet.SocialSources.xiaohongshu /user:session /pass:<session-cookie>`, then
enable the grant. Store the platform's session cookie only — never a password. This is a personal-use
capability: the account, its terms and any consequence of automated access remain the account holder's.

Per-platform session details:

- **B站** — a session is not required for anonymous search, but a signed-in request is materially more
  stable than anonymous traffic (which answers `412` intermittently). The connection sends the anonymous
  device cookie pair **together with** the account session, and its connection test calls
  `/x/web-interface/nav` and reads `isLogin`, so the test costs no search query.
- **小红书** — the search endpoint requires the web client's request signature (`x-s`, `x-s-common`,
  `x-t` and `x-rap-param`). Without it the platform answers a live session with
  `300011 当前账号存在异常`; the same cookie with a correctly signed request returns results. This build
  does not compute that signature yet, so `social.xiaohongshu.search` reports
  `SOCIAL_SESSION_SIGNATURE_REQUIRED` and the session grant should stay disabled.
- **小黑盒** — needs its locally computed `hkey` signature and the web client's fixed query parameters
  (`version=999.0.4`, `web_version=3.0`, `x_app=heybox_website`, the account's own `heybox_id`) in
  addition to the session. A signed request without the session answers `show_captcha`.

> A session grant never enables the matching anonymous reader, and enabling the anonymous reader never
> enables a session search. They are independent capabilities with independent files.
## Build and verification

```powershell
dotnet build plugins/FgoPet.Plugin.SocialSources/FgoPet.Plugin.SocialSources.csproj -c Release -warnaserror
dotnet test tests/FgoPet.Capability.Tests/FgoPet.Capability.Tests.csproj -c Release --filter FullyQualifiedName~SocialSource
```

Unit tests replace the network with a recording handler, so every assertion is local: argument
rejection, grant gating, per-platform isolation, scope staleness, stop behaviour, refusal-to-code
mapping, payload provenance and output sanitization. Protocol tests pin the `hkey` signature against
reference values produced by an independent implementation, verify the `wbi` mixing key derivation, and
parse recorded page/response shapes. No test uses the network, real credentials or production state.
