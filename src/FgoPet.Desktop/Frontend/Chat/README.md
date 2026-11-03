# Native chat frontend

This is the maintained React/TypeScript source for
`plugins/FgoPet.Plugin.Dialogue/Desktop/ui/chat`. The production entry requires
the WebView2 host bridge. Conversation state, approvals, model choices and drafts
remain owned by the C# dialogue services; there is no demo fallback.

Reply Markdown supports source links and inline image previews. HTTP(S) links open in the system browser only after a user click. Completed replies load visible HTTPS image previews through the native host; hidden views and distant history do not download images. WebView's content policy continues to forbid direct remote image/network access. The host returns sanitized PNG pixels in memory, with size, address and timeout limits. A thumbnail can expand in a local dialog; Escape closes only that dialog. A failed preview keeps its original link and a manual retry. Message history retains the original Markdown URL, not downloaded pixels; reopening it may fetch the source again. Unsupported formats remain accessible through their original link.

## Build and check

Use Node 22.18 or newer, then run from this directory:

```sh
npm ci
npm test
npm run build
```

The committed lockfile fixes dependency versions. The build type-checks the
source, generates `dist`, then installs its entry, hashed assets and fonts into
the plugin resource directory. Edit the source and rebuild; do not edit the
generated JavaScript or CSS. Legacy compatibility scripts in the plugin remain
outside the build's ownership.

`npm run test:browser` runs the built output against a local, synthetic WebView2
bridge fixture. Supply an installed Playwright module with
`FGO_PLAYWRIGHT_MODULE` and a compatible Chromium executable with
`FGO_CHROMIUM_EXECUTABLE`, or use a local Playwright installation. This checks
streaming layout, user scroll and text selection, draft preservation, and late
resize replies. It does not run WPF or verify native Windows window geometry.

## Compact presentation

Sending in the compact presentation starts a local reading anchor for the
current turn. Only an explicit action opens the full conversation. The same
conversation and draft remain in place across presentation changes and hiding.

`compactHeight.ts` measures the content's natural height, independently of the
current viewport, and reports 160–360 DIP through the existing `setPresentation`
contract. The native 22 DIP drag strip is outside that range. Reports use a
60 ms throttle with one request in flight and only the latest pending size.
Explicit mode changes cancel pending reports. When content reaches the cap,
the message area scrolls while the composer remains visible.

Presentation requests do not own turn completion or generation errors. A
`CHAT_IDENTITY_STALE` reply to a window adjustment is discarded without retrying
the send or showing a failed-turn card. Other presentation errors reach the
caller's notice without replacing native generation or send/stop errors.

`DialogueWindow.SetPresentation` applies physical-pixel bounds through the
existing monitor/DPI placement code, retaining the bottom and right edges and
manual width during content-only growth. It avoids asynchronous native size
animations so that obsolete targets cannot outlive an explicit mode change.
Windows geometry assertions are in `ChatPresentationWindowTests`; run those on
Windows, including mixed-DPI monitor and small work-area checks before release.
