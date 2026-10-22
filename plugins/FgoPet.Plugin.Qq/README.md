# QQ private chat

## Scope and ownership

This module provides a development preview of a text-only QQ bot C2C entry for one locally paired QQ account and one explicitly linked desktop conversation. It is disabled by default. FGO Pet must remain running on an online Windows computer; the linked role, project and conversation must still be active on the desktop. Opening another conversation does not move the link automatically.

| Owner | Responsibility |
| --- | --- |
| `QqChatPlugin` | Local configuration, account pairing, fixed conversation binding, a bounded serial inbox, receipt admission and application lifetime. |
| `Transport/IQqBotTransport` | QQ bot authentication, WebSocket connection/heartbeat/resume, strict C2C text parsing and passive text replies. It receives a credential target, not an App Secret in channel settings. |
| `QqStateStore` | Protected, atomic local pairing, binding, enabled state and bounded receipt hashes. It stores no chat bodies or App Secret. |
| `Desktop/` | The QQ settings page and its closed local command schemas. It does not accept a remote sender identity or conversation binding from a settings payload. |
| App composition and Dialogue | App maps the QQ records to `IExternalConversationGateway`. Dialogue owns the shared conversation, provider calls, history, clarification, completed-turn observers and existing memory authorization. |

`QqChannelContracts.cs` defines the conversation binding/reply, settings snapshot and content-free diagnostic records. The core module depends on Extensibility and Platform contracts; it does not reference Dialogue or create a second agent. The application supplies the capture/exchange delegates and owns their composition. The desktop project adds the UiSdk settings presentation.

Synchronous container disposal closes message admission immediately and starts cancellation without blocking the desktop dispatcher. Asynchronous disposal awaits the same shutdown and drains the session, including slow or throwing cancellation callbacks. Disposal does not clear the saved pairing or change credentials.

## Configure and pair

Use a QQ bot whose account has access to C2C private messages, with its App ID and App Secret available. Bot provisioning and platform access are outside this module. Configure a working model and open an existing conversation in FGO Pet before pairing; a blank chat without a persisted conversation cannot be linked.

The current UI path is **Settings → Services → QQ**（设置 → 服务 → QQ）. Page controls currently use the English labels below, defined in `Desktop/ui/qq.js`; `Desktop/QqSettingsRegistration.cs` and the shell settings catalog own the entry.

1. Enter **App ID** and **App Secret**, then select **Save bot settings**. A blank secret preserves the saved secret only for the same App ID. Saving disconnects the channel; changing the App ID clears the previous pairing.
2. Keep the intended desktop conversation active and select **Start pairing**. Send the displayed code to the bot in a private QQ chat from the account you want to use. The code expires after five minutes and should stay private.
3. A valid code binds that one sender to the captured role/conversation/project and enables the channel. The pairing exchange does not become a user turn or call the model. Other senders are not admitted to the conversation.
4. To change conversations, first open the desired conversation on the desktop, then select **Use current conversation** in QQ settings. Switching a role, project or conversation without this local action causes scope rejection; the remote message cannot select a different scope.
5. **Disconnect** stops and drains the active connection while retaining the pairing. **Enable** reconnects an already paired account. A saved enabled channel reconnects after application storage is ready on startup; normal message admission still requires the linked desktop conversation to be active.
6. **Clear bot settings & pairing**, followed by **Clear settings**, removes the saved App Secret, pairing and link. The desktop conversation remains. Use this reset before pairing a different QQ account.

The QQ entry reuses the current conversation's model, role, history and already authorized memory. Incoming text and accepted final answers follow the existing Dialogue persistence path and appear in desktop history. The desktop composer draft stays separate. While the desktop is generating or submitting an answer, the external call is rejected as busy.

One clarification started through QQ can be answered with a 1-based option number or free text. Multiple questions direct the user to the desktop; `/取消` cancels the QQ-originated pending question. A desktop-originated question cannot be taken over from QQ. Tool/project authorization, action approval and memory confirmation stay in the existing local flows.

## Transport and delivery boundaries

Desktop image selection stays private to its accepted desktop message and exact native run. QQ text cannot use `image.read` for an unsent desktop selection, and finishing a QQ exchange preserves that selection. The QQ entry does not accept image attachments.

A validated C2C attachment envelope carries only its sender/message identifiers and timestamp to the channel. Attachment URLs, bytes and captions are discarded. Only the already paired sender receives a fixed text-only notice through the existing passive reply path, at most once per 30 seconds per connection session. Message receipts suppress duplicates across reconnect/restart, including uncertain notice delivery. Unpaired senders and pairing attempts receive no attachment notice. No image download, model call or desktop image permission is created.

After a QQ clarification is answered, cancelled or expired on the desktop, the next ordinary QQ message starts a new turn. Explicit replies to the old question remain invalid. Action approval always requires the native desktop confirmation; QQ text cannot grant approval. Completing a question or action confirmation on the desktop does not automatically send its final answer back to QQ. That answer remains in the shared desktop history; the phone receives only the original instruction to return to the desktop.

Gateway close `4009` retains the session and last processed sequence for RESUME. Invalid protocol/configuration/permission closes `4001`, `4002` and `4010`–`4014` stop in Faulted state and require local correction. Invalid sessions `4006`/`4007` still reconnect with IDENTIFY. These classifications follow the [QQ close-code table](https://bot.q.qq.com/wiki/develop/api-v2/openapi/error/error.html) and [RESUME protocol](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/event-emit.html).

Model dispatch accepts only bounded plain-text `C2C_MESSAGE_CREATE` events. Group conversations, attachments, voice and rich-content processing are outside this entry. The channel admits at most 8,192 UTF-16 units of input. It does not import QQ history or receive general Android device data.

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
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~QqSettingsWebPageTests|FullyQualifiedName~NativeConversationIntegrationTests.External_"
```

These cover default-off behavior, pairing and sender isolation, durable deduplication, cancellation/drain, transport parsing/reconnect/reply limits, secret handling, exact conversation binding, desktop draft/history reuse and clarification ownership with synthetic data. They do not establish native Windows UI, actual QQ account eligibility or real-provider delivery. Windows and Android-to-QQ end-to-end acceptance remain pending, including pairing, scope changes, disconnect/restart, a normal reply and clarification within the passive reply window. This preview does not change the repository's current release claim.
