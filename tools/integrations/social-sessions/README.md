# Social source sessions (personal use)

The social source plugin can search 小红书 / 小黑盒 / B站 with **your own signed-in session**. This
directory contains the only host-side entry point for that, because the settings page does not exist yet.

Read the risk note first: a session searches as your account. The account, the platform's terms and any
consequence of automated access are yours. Store a session only for a personal deployment, never in a
build you distribute.

## 1. Get the cookie from your own browser

1. Open the platform and sign in.
2. Open DevTools (`F12`) → **Network**.
3. Reload the page and click a request to that platform's domain (any document or XHR request works).
4. In **Request Headers**, find `Cookie:` and copy **the whole value** — every `name=value` pair.

Pasting the whole header is intended. The plugin adds its own anonymous device cookie where a platform
needs one (B站), and unrelated cookies are harmless.

> Never paste a password. The script refuses a value containing `password`, `passwd` or `pwd`, and
> rejects control characters and values over 8192 characters.

### Check the cookie before storing it

The checker reports which expected cookie **names** are present. It never prints values, so its output is
safe to share.

```powershell
pwsh -File tools/integrations/social-sessions/Test-SocialCookie.ps1 -Cookie '<paste here>'
```

Expected for a signed-in session:

| Platform | Required names |
| --- | --- |
| bilibili | `SESSDATA` (the actual login token), plus `DedeUserID` |
| xiaohongshu | `web_session`, plus `a1` |
| xiaoheihe | `heybox_id`, plus `pkey` |

If `SESSDATA` is missing on B站, that browser profile is not signed in for that tab, or the DevTools
value was truncated. `SESSDATA` is HttpOnly, so it does **not** appear under DevTools → Application →
Cookies, but it **does** appear in the `Cookie:` request header — which is why this guide uses the
header. Do not substitute the Application tab.

Tip: in DevTools click the `Cookie` value cell, press `Ctrl+A` then `Ctrl+C` to copy the whole value.
Copying with the mouse easily truncates a long header.

## 2. Configure

### Preferred: fill in `.env`

`.env` beside this README is already created and **ignored by git**. Paste one cookie per line, leave the
platforms you do not use empty, save, then run:

```powershell
# validate only — reports names and lengths, writes nothing
pwsh -File tools/integrations/social-sessions/Import-SocialSessions.ps1 -WhatIfOnly

# apply
pwsh -File tools/integrations/social-sessions/Import-SocialSessions.ps1
```

Each non-empty entry is written to Credential Manager and gets its own grant. Empty entries are
skipped, so an unused platform stays unconfigured. A missing login token is refused rather than stored —
that would only fail later with a confusing error.

A platform whose session search this build cannot complete yet — currently 小红书, whose endpoint needs
the web client's request signature — keeps its validated cookie in place but gets a **disabled** grant.
Re-running the import therefore never re-enables a capability that can only be refused; see §3 for the
error code it reports if it is enabled by hand.

The import prints platform names, cookie **names** and character counts. It never prints a value, so its
output is safe to paste into a chat.

### Single platform on the command line

```powershell
pwsh -File tools/integrations/social-sessions/Set-SocialSession.ps1 `
    -Platform xiaohongshu `
    -Cookie '<paste the Cookie header value here>'
```

## 2a. What "required" means

`-Platform` accepts `bilibili`, `xiaohongshu` or `xiaoheihe`.

What it does, in this order:

1. Writes the cookie into Windows Credential Manager as a generic credential named
   `FgoPet.SocialSources.<platform>`, using the same Win32 call the application reads it with.
2. Writes `social-session-<platform>.json` in the app state root. That file contains **no secret** —
   only `Enabled`, a fresh `AuthorizationId`, an incremented `AuthorizationVersion` and `Platform`.
   A copied settings file therefore grants nothing.

The state root is `%LOCALAPPDATA%\FgoPet` unless `FGO_PET_STATE_ROOT` is set. Pass `-StateRoot` to
override it (useful when running from a portable/unpacked build).

## 3. Verify

Restart the app if it is running, then from the host side:

```csharp
plugin.GetSessionSnapshot("xiaohongshu");           // configured / enabled, reads no secret
await plugin.CheckSessionAsync("xiaohongshu", token); // one fixed neutral query, no source text returned
```

Interpretation:

| Result | Meaning |
| --- | --- |
| `available=true` | the platform accepted the session |
| `SOCIAL_SESSION_MISSING` | no credential under that target name |
| `SOCIAL_SESSION_EXPIRED` | the session was accepted as a login but has expired (`-100`) |
| `SOCIAL_SESSION_INVALID` | the platform does not accept the value as a session (`-101`, or a malformed cookie) |
| `SOCIAL_SESSION_CHANGED` | the grant or scope changed during the call; the result was discarded |
| `SOCIAL_SESSION_NOT_CONFIGURED` | no grant file |
| `SOCIAL_RATE_LIMITED` | the platform is throttling this account |
| `SOCIAL_SESSION_SIGNATURE_REQUIRED` | 小红书 answered `300011 当前账号存在异常` for a live session: the endpoint needs the web client's request signature (`x-s` / `x-s-common` / `x-t` / `x-rap-param`) and this build does not compute it yet. Not a stale cookie and not a flagged account — do not retry; leave the grant disabled |
| `SOCIAL_UPSTREAM_REFUSED` | any other platform refusal; no source text is returned with it |

Nothing is retried automatically. A rejected session is dropped from memory, so the next attempt reads
the credential again.

## 4. Revoke

```powershell
pwsh -File tools/integrations/social-sessions/Set-SocialSession.ps1 -Platform xiaohongshu -Clear
```

This deletes the credential and rewrites the grant as `Enabled: false`. The order matters: a disabled
grant without a secret is a safe state, a live secret without a grant would not be.

## Per-platform notes

- **B站** — a session is optional for search but clearly more stable than anonymous traffic, which
  answers `412` intermittently. The connection sends the anonymous device cookie pair together with
  your session, and its connection test calls `/x/web-interface/nav` to read `isLogin`, so testing costs
  no search query.
- **小红书** — the search endpoint requires the web client's request signature (`x-s`, `x-s-common`,
  `x-t`, plus the search-specific `x-rap-param`). Reproduced without it: no session answers
  `-101 无登录信息`, a live session answers `300011 当前账号存在异常`, and the same cookie with a signed
  request returns results. The signature is not implemented here yet, so the session check reports
  `SOCIAL_SESSION_SIGNATURE_REQUIRED`.
- **小黑盒** — needs its locally computed `hkey` signature and the web client's fixed query parameters
  (`version=999.0.4`, `web_version=3.0`, `x_app=heybox_website`, the account's `heybox_id`) in addition
  to the session. A correct `hkey` with a session returns `result.items`; the platform reports
  `show_captcha` for anonymous traffic.

## Why a session is separate from the anonymous readers

The anonymous reader and the session search are independent capabilities with independent grant files,
independent connection objects and independent error codes. Enabling one never enables the other:
`social.bilibili` still requires its own anonymous grant even when `social.bilibili.search` is
configured, and vice versa.
