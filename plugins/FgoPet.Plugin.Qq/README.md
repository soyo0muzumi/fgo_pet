# QQ private chat

## Scope and ownership

This module provides a development preview of a QQ bot C2C entry for one locally paired QQ account. New pairings authorize a fixed role/project and use an independent conversation. It is disabled by default. FGO Pet must remain running on an online Windows computer. Desktop chat/role/project selection does not move the QQ scope or change its conversation. Protected legacy desktop-linked bindings retain their previous behavior until the user locally selects **Use current role and project**; upgrading does not silently broaden an existing grant.

| Owner | Responsibility |
| --- | --- |
| `QqChatPlugin` | Local configuration, account pairing, fixed conversation binding, a bounded serial inbox, receipt admission and application lifetime. |
| `Transport/IQqBotTransport` | QQ bot authentication, WebSocket connection/heartbeat/resume, strict C2C text/single-image envelopes and passive text replies. It receives a credential target, not an App Secret in channel settings. |
| `Transport/QqImageDownloader` | Bounded PNG/JPEG download from exact official QQ attachment origins, without bot credentials, redirects or cookies. The host invokes it only after sender, receipt and freshness admission. |
| `QqStateStore` | Protected, atomic local pairing, binding, enabled state and bounded receipt hashes. It stores no chat bodies or App Secret. |
| `Desktop/` | The QQ settings page and its closed local command schemas. It does not accept a remote sender identity or conversation binding from a settings payload. |
| App composition and Dialogue | App maps independent bindings to `IIndependentConversationGateway` and legacy bindings to `IExternalConversationGateway`. Dialogue owns conversation isolation, provider calls, history, clarification, completed-turn observers and existing memory authorization. |

`QqChannelContracts.cs` defines the conversation binding/reply, settings snapshot and content-free diagnostic records. The core module depends on Extensibility and Platform contracts; it does not reference Dialogue or create a second agent. The application supplies the capture/exchange delegates and owns their composition. The desktop project adds the UiSdk settings presentation.

Synchronous container disposal closes message admission immediately and starts cancellation without blocking the desktop dispatcher. Asynchronous disposal awaits the same shutdown and drains the session, including slow or throwing cancellation callbacks. Disposal does not clear the saved pairing or change credentials.

## Configure and pair

Use a QQ bot whose account has access to C2C private messages, with its App ID and App Secret available. Bot provisioning and platform access are outside this module. Configure a working model and select the intended role and optional project in FGO Pet before pairing. A blank desktop chat can be paired; no desktop message is required.

The current UI path is **Settings → Services → QQ**（设置 → 服务 → QQ）. Page controls currently use the English labels below, defined in `Desktop/ui/qq.js`; `Desktop/QqSettingsRegistration.cs` and the shell settings catalog own the entry.

1. Enter **App ID** and **App Secret**, then select **Save bot settings**. A blank secret preserves the saved secret only for the same App ID. Saving disconnects the channel; changing the App ID clears the previous pairing.
2. Keep the intended role and project selected and select **Start pairing**. Send the displayed code to the bot in a private QQ chat from the account you want to use. The code expires after five minutes and should stay private.
3. A valid code binds that one sender to the locally captured role/project and a fresh random session identity, then enables the channel. Pairing does not become a user turn, create a conversation or call the model. Other senders are not admitted. The first ordinary QQ message creates its conversation; `/新对话` clears only QQ's selection so the next message creates another. Old history remains available on the desktop.
4. To change QQ's role/project or upgrade a legacy binding, select the desired scope on the desktop, then **Use current role and project** in QQ settings. This starts a fresh independent session. Remote text cannot select a different role/project. Re-pairing also generates a fresh session identity, so a different paired account does not inherit the previous QQ conversation.
5. **Disconnect** stops and drains the active connection while retaining the pairing. **Enable** reconnects an already paired account. A saved enabled channel reconnects after application storage is ready on startup. Its independent conversation persists across restarts; legacy bindings still require the linked desktop conversation to be active.
6. **Clear bot settings & pairing**, followed by **Clear settings**, removes the saved App Secret, pairing and link. The desktop conversation remains. Use this reset before pairing a different QQ account.

The QQ entry reuses the configured model, captured role and existing authorization policies. Incoming text and accepted final answers follow the existing Dialogue persistence path and appear in desktop history. Desktop drafts, selected images, conversation state and cancellation are separate. Independent foreground requests share the data-maintenance and background-review fence: deletion/maintenance stops and drains both owners. A new QQ message does not take over a pending desktop question or action.

One clarification started through QQ can be answered with a 1-based option number or free text; `/取消` cancels the QQ-originated pending question. Multiple-question forms must be cancelled and initiated on the desktop. A desktop-originated question cannot be taken over from QQ. Independent QQ actions needing approval are cancelled without execution and instruct the user to initiate and confirm the action on the desktop. QQ does not grant local directory, tool or memory permissions.

## Transport and delivery boundaries

Desktop image selection stays private to its accepted desktop message and exact native run. QQ text cannot use `image.read` for an unsent desktop selection, and finishing a QQ exchange preserves that selection. Independent bindings accept one PNG/JPEG attachment and optional caption, up to 8 MiB / four million pixels, on the existing supported image/model routes. Bytes are decoded and re-encoded in memory, stripping metadata, and granted only to that QQ message/run. Model/route changes, expiry and completion revoke the remote grant. Provider image disclosure and local Buddy persistence caveats remain those of the existing desktop image route.

Only validated single-image envelopes retain a bounded caption and attachment URL. Download is limited to HTTPS port 443 at `multimedia.nt.qq.com.cn` or `gchat.qpic.cn`, refuses local-network resolved addresses, pins the connection address, denies redirects, bounds streamed bytes and the full download duration, and sends no bot credentials. Other media and legacy bindings receive a rate-limited unsupported notice without download or model dispatch. Receipts suppress duplicates across reconnect/restart, including uncertain delivery. Unpaired senders and pairing attempts receive no attachment notice, image download or model call. Attachment URLs and image bytes are not persisted by the QQ plugin or recorded in diagnostics.

For legacy shared bindings, completing a QQ-originated question on the desktop clears its remote ownership; the next ordinary QQ message starts a new turn. Explicit replies to an old question remain invalid. Desktop completion has no unsolicited QQ delivery callback. Independent sessions keep their own pending question in the QQ owner; they cannot take over desktop confirmation or accept remote action approval.

Gateway close `4009` retains the session and last processed sequence for RESUME. Invalid protocol/configuration/permission closes `4001`, `4002` and `4010`–`4014` stop in Faulted state and require local correction. Invalid sessions `4006`/`4007` still reconnect with IDENTIFY. These classifications follow the [QQ close-code table](https://bot.q.qq.com/wiki/develop/api-v2/openapi/error/error.html) and [RESUME protocol](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/event-emit.html).

Model dispatch accepts only bounded text or supported single-image `C2C_MESSAGE_CREATE` events. Group conversations, other attachments, voice and rich-content processing are outside this entry. The channel admits at most 8,192 UTF-16 units of text. It does not import QQ history or receive general Android device data.

Inbound admission and passive reply delivery use a conservative **five-minute window measured from the incoming message timestamp**. The queue rechecks message age before dispatch. A model exchange has a four-minute cancellation deadline, which does not extend the reply window. Expired messages are dropped, and expired replies do not fall back to unsolicited messages. This five-minute limit is the implementation's conservative bound, not a claim about every QQ account's platform entitlement.

The WebSocket callback only admits to the bounded inbox, so generation does not hold up heartbeats. Receipt hashes are persisted before model dispatch; repeated admitted messages are not regenerated after reconnect or restart. Delivery with an unknown outcome is not blindly retried. Replies are tied to a message received by that transport instance and the same sender, with stable `msg_id`/`msg_seq`. The channel does not keep a durable offline queue; events received during reconnect still pass freshness and deduplication checks.

The QQ response is limited to 12,000 UTF-16 units and split into at most three text replies without breaking surrogate pairs. A longer response is shortened for QQ and ends with “（回复较长，完整内容已保存在电脑上的对话中。）”; the full accepted final answer stays in desktop conversation history. This display limit does not trigger another model call or a proactive-message fallback.

## Credentials and privacy

The production host uses Windows Credential Manager for the App Secret and the existing `WindowsStateProtector` with DPAPI CurrentUser for `Qq/channel.protected`. Protected state holds the bot-scoped paired identity, conversation binding and receipt hashes; it has no plaintext fallback. The temporary pairing code is held in memory and expires locally. Settings snapshots expose only whether a secret exists, and the page clears its secret input after a successful save or disposal.

QQ carries the admitted text exchange. The module does not send model reasoning, internal tool transcripts or unrelated conversation history to QQ. Diagnostics contain time, a hashed correlation value, stage and fixed safe error codes, without raw sender/message identifiers, message bodies, credentials or exception text.

## Validation and remaining acceptance

Run the pure .NET channel and synthetic HTTP/WebSocket tests:

```powershell
dotnet test tests/FgoPet.Qq.Tests/FgoPet.Qq.Tests.csproj -c Release
```

On Windows, run the settings and shared Dialogue integration tests:

```powershell
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~QqSettingsWebPageTests|FullyQualifiedName~NativeConversationIntegrationTests.External_|FullyQualifiedName~NativeConversationIntegrationTests.Independent_|FullyQualifiedName~DialogueContextLifetimeTests|FullyQualifiedName~ImageToolsPluginTests"
```

These cover default-off behavior, pairing/sender isolation, fresh session identities, durable image/text deduplication, cancellation/drain, transport parsing/reconnect/reply limits, bounded downloads, secret handling, legacy exact binding, independent creation/reset/restart, desktop history/draft/image isolation, image clarification lifetime, local approval refusal and maintenance fencing with synthetic data. They do not establish native settings UX, actual QQ account eligibility or real-provider image delivery. Windows and Android-to-QQ acceptance remain pending, including pairing, role/project capture, legacy opt-in, disconnect/restart, image captions and clarification within the passive reply window. This preview does not change the repository's current release claim.
