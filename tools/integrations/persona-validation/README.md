# P2 isolated provider budget support

`P2BatchProvider.cs` is non-production validation support, linked into `FgoPet.App.Tests`.
It reuses `IChatProvider`, `OpenAiCompatibleChatProvider`, the production request writer/parser,
and `ICredentialReader`. It has no application DI registration, provider discovery probe,
automatic retry or dependency installation. The opt-in subscription runner resides only in the
test assembly and reuses `WindowsCredentialStore`; default test discovery skips real calls.

## Approval and ownership

1. Freeze the synthetic fixtures, Persona, protocol, source/binary versions and hash them into
   `FixtureFingerprint`. Choose one isolated ledger path and a short approval expiry.
2. `CreateUnapproved` creates a new journal with `FileMode.CreateNew`. It cannot send requests.
   Obtain `ApprovalFingerprint`, which binds the frozen policy, canonical ledger path and a
   newly generated journal nonce. Close the preparation handle.
3. Only after explicit user approval may the operator pass that exact fingerprint to `Open`.
   It is an operator-held approval marker, never a tool/model argument or an authentication key.
   Opening an existing journal does not automatically restore approval.
4. Give **the same instance** to every provider consumer in the isolated composition, including
   primary replies, tool continuations, fallback/retries, summary, recall and any enabled observer.
   Disable unplanned background consumers. This does not protect the running production app or
   other connections. Do not substitute an ordinary provider when the batch gate rejects a call.
5. An approved runner must use `CreateDirectTransport` (redirects and proxies disabled, no retry handler)
   and the existing approved credential owner. Custom transport injection is for offline tests.

Never recreate a missing/corrupt journal on resume. A replacement journal has a new approval
fingerprint even at the same path. Do not restore an earlier journal snapshot to regain budget.
The local journal is a durable operator-owned guard, not a tamper-resistant account billing system.

## Official direct-route limits

- At most 20 durable dispatch reservations per batch and at most 3,000,000 micro-CNY (CNY 3).
  A policy can lower either limit. The proposed ceiling is not spending authorization.
- One in-flight request; concurrent calls are refused. No network `/models` call: returned
  model capacity is frozen documentation metadata, not live server discovery.
- Fixed official DeepSeek endpoint/model and explicit output cap from 1 to 2,048 tokens.
  The dated peak tariff and its expiry are compiled into the policy. Refresh/review the tariff
  and obtain a new approval if it expires or the model, price, currency or frozen inputs change.
- Before HTTP dispatch, flush the count and conservative cost reservation to disk. Reserve the
  full documented context window (padded to 1,048,576 tokens) plus the requested output limit,
  at uncached peak rates. Never treat the local token estimate as a guaranteed tokenizer bound.
- Settle only after auditing complete final SSE usage and observing the production parser's
  terminal chunk. Settlement happens before yielding that chunk, because consumers break there.
  Reasoning tokens are already included in completion tokens under the frozen API contract.
- HTTP failure, cancellation after reservation, premature disposal, malformed/missing usage,
  truncated response or crash retains the full reservation and stops the batch. Request counts
  are never refunded. An uncertain send is not made free by `RequestWasSent == false`.
- Before settlement, buffer at most 8 MiB of response data and enforce the existing line
  bound of 512 × 1024 UTF-16 characters. This does not validate incremental streaming latency or desktop UX.

Costs are bounded under the frozen official CNY token tariff and provider context/output contract;
the guard cannot cap charges from other applications or guarantee unobserved account billing rules.
Confirm the account tariff/currency before approving a real batch. No discounts are assumed.

The journal records only timestamps, sequence/attempt numbers, policy hashes, nonce, status,
token counts and micro-CNY amounts. It never records credentials, prompts, conversations,
tool arguments, response text or the raw ledger path.

## Existing local subscription route

`LocalSubscription = true` is a separately fingerprinted, explicitly unpriced policy. It fixes
the endpoint to `http://127.0.0.1:8326/v1/chat/completions`, model to
`buddy/deepseek-v4.1-flash`, and credential target to the existing
`fgo-pet/provider/custom-openai-compatible` owner. No production setting or credential is saved.
It preserves approval, expiry, nonce/path binding, one in-flight request, durable counting,
restart refusal, bounded response audit and unknown-result stopping. The serialized request
is limited to 32,768 UTF-8 bytes and requested output to 2,048 tokens. A conservative 131,072
context value is test metadata, not live model discovery.

A local-subscription terminal `length` stops the batch directly from the usage-audited wire
response. Its known usage is settled, then the ledger is durably revoked before the terminal
chunk is yielded. Missing, delayed or partial gateway diagnostics and diagnostic callback
failures cannot allow another HTTP send. Unknown usage still retains its reservation and stops
through the existing unknown-result path. This adds no retry or finish-code mapping change.

Native tool selection and final output are separate request phases. A tool request offers
`response.finalize` with the business tools and appends only the tools-phase instruction. The
model ends selection by calling it alone with `{}` and no text. The next request offers no tools
and appends the fixed JSON template after all other messages, including fresh context and
tool observations. With no available tools, the first request is already final output. The host
does not generate or discard a draft; a compliant final is delivered immediately. A tool-stage
answer fails instead of becoming a draft. The existing tool-rejection fallback is final output.
Only an invalid final uses the existing single tool-free format repair, with the template last.
Every actual provider request, including selection and repair, remains visible in the ledger
and counts toward the batch cap. The control call also consumes the run's tool-call allowance.
Strict final diagnostics apply the Native JSON contract; the separate compatibility probe
describes ordinary-chat parsing and cannot establish that a Native final passed validation.

Completed, settled tool responses rejected by a side observation using the existing Native
finalize validator attach `FinalizeRejection` to the request diagnostic: call count, whether
the offered control resolved, whether the body is whitespace only, and whether every resolved
control's arguments are a valid empty JSON object. No body, argument values or names are retained
in this state. Batch/attempt and the existing response hash provide request association. This
observation is not proof that the host reached its validator: correlate the run error separately.
Incomplete responses, earlier protocol errors, valid controls and unresolved aliases have no
rejection state. Observation failure leaves a gap without changing chunks, accounting, retries,
cancellation or iterator disposal; it does not request repair or stop an otherwise valid batch.

The default count limit is **20 application-to-gateway sends**, including application continuations
and retries. For an explicitly authorized uncapped local-subscription experiment, an operator may
freeze `MaxRequests = null` in a new policy and fixture manifest. This removes only the test batch
request-count ceiling; the contract runner must also disable its extra per-scene count check.
The null policy has a different fingerprint, cannot reuse an old ledger/approval, and is rejected
for the official billed route. Expiry, revocation, unknown-result stopping, durable counting,
route restrictions, per-request bounds and the native AgentRun limits still apply.
Uncapped ledgers replay incrementally with a 2,048-character entry bound; the old 64 KiB
total-file guard remains for capped policies. Approval expiry and all entry validation still apply.
No production setting changes. Ordinary fixtures keep the original 20-send policy.
The existing gateway can retry or refresh its own upstream credentials; its
upstream request count and subscription charges are unknown. This mode does not reuse the
official tariff or claim a monetary cap. `CommittedMicros` is null; journal `Micros = 0` is an
unused monetary field, never evidence of free service. Official route cost checks remain intact.

The isolated Native subscription entry is
`NativeConversationIntegrationTests.P2_subscription_explicit_frozen_live_batch`. It requires
an operator-supplied `FGO_PET_P2_SUBSCRIPTION_MANIFEST` under ignored `docs/internal/reports`,
an explicit authorization marker, short expiry, fixture/Persona hashes, and every FgoPet test
dependency DLL hash. A fresh journal uses `CreateNew`; rerunning the same batch cannot reset
its limit. Ordinary tests do not supply this environment variable. Real execution requires
current user authorization for this exact connection and fixture batch; an environment variable
is an operator mechanism, not permission in itself. Never expose it as an application/model tool.

Only source Persona and synthetic inputs are used, with a separate temporary SQLite database
per scene, a local read-only fixture tool and a passive observer. No production history,
Memory, Understanding worker, review, external tool or desktop UI is started. Safe events and
hash/count results exclude text. Synthetic final replies alone may be retained separately in
`synthetic-replies.txt` for human inspection; this is not a runtime log or a semantic PASS.
Ordinary-route model semantics, installed Persona, desktop UX and human acceptance remain separate.

The fixture tool is registered only for tool-failure and counterevidence scenes. Correction,
history reopening and explicit-help scenes offer no tools. Unexpected tool context stops the
batch before another dispatch; required evidence and output consumers remain checked. The
five scenes retain their original ten inputs and the diagnostic follow-up for call/run status
and UTC+08:00 conversion. Two explicit retry turns follow: the read still fails once, then the
fixture is restored and read once successfully. A sixth scene offers no tools and asks about
retrying an unknown command result and a committed effect with missing output; these are rule
questions, not actual command-execution tests. A seventh no-tool turn requests literal delivery
markup and JSON examples between ordinary prose; both examples and surrounding text must remain.
The sixteen-turn scenario set no longer fits one 20-send batch after explicit phase selection.
The full synthetic regression uses two independent batches, each with the original 20-send cap;
the explicit-retry synthetic regression covers the first four scenes within one capped batch.
The real entry still has one shared 20-send cap and never silently opens a second batch. A future
real run needs a newly frozen scope and manifest. Each turn checks its exact read count;
omission or an unrequested/repeated read stops the batch. Safe results retain per-turn
read counts, offered-tool counts and historical status/UTC metadata. Final-format evidence adds
only a raw-final hash, shell shape flags and a body-change flag; raw model finals are not logged.
The literal-example check distinguishes an intended quotation from accidental shell output. Final-reply semantics still
require human review; offline scripted replies do not prove that a model will choose to retry.

Failed sends also retain content-free delivery diagnostics before the success assertion: run
status/hash, final length/hash and JSON validity, strict/lenient validation outcomes, and separate
accept/publish/observer outcomes. The test decorator never formats logger state or exception
messages. These diagnostics are not registered in the product and do not retain raw failed finals.
The synthetic runner stops on an unrequested whole delivery shell, even when storage and observer
delivery succeeded; a completed execution is not a semantic acceptance result.

An explicitly authorized diagnostic continuation uses
`P2_continuation_explicit_frozen_live_phase` and `FGO_PET_P2_CONTINUATION_MANIFEST`.
Its fixed report directory contains one immutable budget and one unique ledger. A read-only audit
holds the predecessor manifest and ledger locked, verifies their hashes and replays the settled
count without credentials or transport. The original three sends leave at most seventeen sends
in the child ledger. Separate frozen phase manifests permit one diagnostic send and sixteen
verification sends; a pre-dispatch gate enforces each phase limit. Verification can only reopen
the same ledger at its expected count; repeating creation, changing its policy, missing usage or
restarting at another count cannot refund or reset the allowance. Binary hashes are frozen per
phase, independently of the immutable budget. Default tests skip this entry. Any further real
batch still requires user authorization; an offline fix is not proof of a post-fix model result.

## Offline verification

From the repository worktree, with the existing SDK/packages:

```powershell
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter '(FullyQualifiedName~P2BatchProviderTests|FullyQualifiedName~P2_budget|FullyQualifiedName~P2_subscription_offline|FullyQualifiedName~P2_subscription_fixture|FullyQualifiedName~P2_delivery_diagnostics|FullyQualifiedName~P2_continuation)&Category!=LiveP2Subscription'
```

Every test selected by this offline command uses a synthetic credential reader and an in-memory terminal HTTP handler; no socket
or real provider is used. Tests include ordinary/Native orchestration, tool continuation, runtime
fallback, production summary/recall consumers, persisted limits and refusal/failure boundaries.
Optional `FGO_PET_P2_BUDGET_EVIDENCE` points only to an ignored evidence directory for safe,
synthetic envelope measurements; it is not an application setting or a real-call switch.


The contract batch emits `provider-diagnostics.jsonl` with a hashed batch ID, attempt number,
UTC time, fixed stage/status/reason categories and optional HTTP status. It distinguishes
transport, HTTP, response read, usage validation, cancellation/timeout, adapter and consumer
abandonment outcomes. Usage checks retain their strict acceptance and durable stop rules;
no exception message, response body, prompt, credential, URL or tool arguments are logged.
Diagnostic sink failure does not refund, retry, authorize or change settlement. An unavailable
sink may leave a diagnostic gap; the ledger remains authoritative for sends and settlement.
Historical approval metadata says `NotIncludedInThisProjection`: absence from a projection
is not absence of approval or stored records. This contract does not expose or grant approval.

## 跨层结束诊断（仅隔离验证器）

`P2ResponseDiagnostics.cs` 是测试旁路，不注册到 FGO Pet 产品。响应完成缓冲后、原有
usage 审核之前，`provider-diagnostics.jsonl` 的 `Response` 增加正文 SHA-256、UTF-8
字节数、UTF-16 代码单元数、完整性及固定解析状态；只累计 SSE 的 `delta.content`，
不累计 reasoning 或工具参数。编码使用 UTF-8 替换规则，不做规范化或重序列化。
完整性要求 DONE、非空结束码且没有损坏帧或 DONE 后数据；失败时仅有前缀摘要或 null。
原有 usage、预算、取消、拒绝及停批规则不变，摘要不能代替它们。

SSE 响应 ID 只接受 1–256 个可打印 ASCII 字符，落盘前作 SHA-256，与插件的
`requestHash` 对齐。`ResponseIdState` 区分 consistent、partial、missing、invalid、
conflicting；缺失、非法或冲突时不会按时间、正文或提示词猜测请求 ID。
正文 JSON 失败仅保留零基 `ErrorLine`、行内 UTF-8 `ErrorByteInLine`、绝对 UTF-8
`ErrorByteAbsolute` 及 `ErrorAtEof`；不保留异常消息、Path 或错误附近字符。
UTF-16 长度不可直接与 UTF-8 错误位置比较。

`model-finals.jsonl` 的 `Correlation` 对收到的原始 final 字符串再作相同摘要，
`WireBodyMatches` 仅在完整 wire 摘要可用时比较。每个用户轮次生成独立 scope，
只在实际 Native 日志事件 4103 后登记修复父请求，再由同一 scope 的下次派发消费一次。
`RepairParentRequest`、`RepairParentHash` 标识该父子关系；累计 delivery 快照中重复
出现的 repair 阶段不会制造新请求。关联状态最多保留 64 个请求和 64 个待关联 scope，
跨轮次不会继承。已有并发拒绝规则继续生效。摘要和关联哈希仍属于本机诊断数据，
只保留在有界证据目录，不能视为正文已匿名化或模型语义已验收。

`finish-diagnostics/FinishDiagnostics.Offline.csproj` 仅重新编译测试和上述支持文件，
引用已冻结的测试依赖 DLL，没有 ProjectReference 或 PackageReference；输出到另一个
被忽略目录。原有 41 个 FgoPet DLL 不重建、不覆盖。下面命令依赖本机已有 SDK、
NuGet 缓存及冻结 runtime；不得为这次检查安装依赖或启用真实入口：

```powershell
$evidence = Join-Path $PWD 'docs/internal/reports/finish-diagnostics-offline'
$project = 'tools/integrations/persona-validation/finish-diagnostics/FinishDiagnostics.Offline.csproj'
dotnet restore $project --source "$env:USERPROFILE/.nuget/packages" -p:NuGetAudit=false "-p:BaseOutputPath=$evidence/build/bin/" "-p:BaseIntermediateOutputPath=$evidence/build/obj/"
dotnet build $project -c Release --no-restore "-p:BaseOutputPath=$evidence/build/bin/" "-p:BaseIntermediateOutputPath=$evidence/build/obj/"
$env:FGO_PET_FINISH_V2_EVIDENCE = Join-Path $evidence 'checks'
# 先由插件的 buddy-finish-diagnostics 单测导出纯合成的 cross-layer-fixtures.json。
# 该夹具包含合成 SSE 正文，是测试输入，不是运行诊断日志。
$env:FGO_PET_FINISH_V2_FIXTURES = '<已生成的合成夹具绝对路径>'
dotnet "$evidence/build/bin/Release/net8.0-windows10.0.17763.0/FgoPet.App.Tests.dll"
```

该可执行入口只列举纯离线回归，使用内存 HTTP handler 和合成凭据，不调用测试发现中的
真实批次入口。覆盖跨 Node/.NET 摘要、CJK/emoji/换行错误位置、真实 Native 格式修复
关联、ID 缺失/冲突、轮次隔离、损坏 SSE、诊断 sink 失败及未知用量停批；它不证明
新诊断已部署，也不能追溯此前没有保存摘要的真实坏 JSON 或 length 请求。

## 结束调用、宿主轮次及派发前拒绝（仅隔离验证器）

完整结束调用被原 `NativeFinalization.Validate` 拒绝时，`FinalizeRejection` 保留调用数、
是否解析到结束工具、正文是否仅空白及参数是否均为合法空对象。`ArgumentStates` 按已解析的
结束调用顺序记录固定枚举：`InvalidJson`、`NonObject`、`NonemptyObject`、`EmptyObject`。
它使用与 Native 相同的 JSON 解析及根对象判定，不记录参数键名、值、摘要或异常原文；
只说明验证器收到的参数形态，不能据此定位上游转换层或回填历史请求原因。

contract 操作器为每个宿主轮次生成独立 `HostTurnHash`，在 request 局部上下文中传递。
provider 诊断的 `HostTurnHash/Attempt/Response.RequestHash` 与 delivery 的
`HostTurnHash/Turn/RunHash/RunErrorCode` 直接连接，不用时间或正文猜配。
同轮的工具、阶段选择、最终输出及格式修复共用该标识；下一轮生成新标识。
关联上下文缺失、非法或读取失败时只记录 null；wire ID 不可用时 `RequestHash` 保持 null，
不借用旧请求。上下文不进入模型消息、HTTP 载荷、预算或重试判定。

`ledger.Enter` 在凭据及 transport 访问前抛出的预算异常，另外产生
`Stage=pre-dispatch, Status=rejected, Attempt=0` 的诊断，`PreDispatchRefusal` 仅含
观察时已预约数量 `ReservedRequests` 和白名单安全错误码（如 `P2_REQUEST_LIMIT`）。
此时没有新预约、HTTP 请求或响应 ID；`Attempt=0` 不能计作一次真实网关请求。
次数上限仍由原门禁检查；达到上限不必修改 `Snapshot.StopCode`。诊断 sink 失败不替换
原异常，不执行未成功 Enter 的 Exit，不结算、退款、重试或放行其他请求。

新增纯离线回归位于 `NativeConversationIntegrationTests.RequestDiagnostics.cs`，覆盖
宿主错误与请求关联、缺失 ID、轮次隔离、并发拒绝、20 次上限及 sink/上下文失败和取消。
原结束诊断回归继续核对 Native 错误及工具接收状态。离线用例只输出脱敏状态和关联字段，
不启用下述候选原文留存入口；模板、skill、2048 上限及既有修复策略保持原值。

## 失败候选留存模式

contract 操作器固定启用 `P2FailedCandidateCapture(..., metadataOnly: true)`，包括真实和离线
contract 场景。失败候选只写 `failed-candidates.metadata.jsonl`：包含请求关联、验证状态、
正文摘要及固定模板的位置／角色／种类／摘要，不包含候选原文、UTF-16 Base64 或模板文字。
成功回复不再写 `synthetic-replies.txt`；验收只保留原有 final/delivery 的状态和摘要。
这不改变模型请求、Native 输出校验、一次格式修复或预算／停批规则；metadata 写入失败
仍使用原有 fail-closed 路径。正文留存被关闭后不能用日志评价回复措辞或还原原始候选。

下面的 private 模式仅保留给显式调用默认构造参数的既有合成离线夹具；contract 操作器不使用它。

仅既有显式合成离线夹具可在格式修复前，将严格验证失败的候选写入独立的
`failed-candidates.private.jsonl`。这不是脱敏运行日志：文件含模型候选原文，只能本地保留，
不得提交、上传或作为公共报告。创建时即禁止 ACL 继承，仅当前用户、SYSTEM 和 Administrators
可访问；使用 CreateNew，不覆盖旧证据，每条记录落盘后才继续。普通 provider/final/delivery
日志仍只记录摘要，不复制原文。

记录包含批内请求序号、轮次 scope 哈希、响应 ID 哈希、修复父请求、正文摘要及实际 request
中匹配固定白名单的格式模板（含消息位置和模板摘要）。不保存完整 Prompt、persona、历史、
用户输入、工具参数或凭据。可读 `RawText` 保留合法 Unicode 原文；UTF-16LE 代码单元的
Base64 及独立哈希保证 BOM、换行、NUL 和孤立代理项也能无损还原。只有失败候选落盘，
合规候选不额外生成或留存正文。每批关联状态最多 64 项，不跨 scope 猜配。

工具阶段没有最终 JSON 模板；留存侧允许以工具阶段指令和实际 offered tools 共同识别它。
最终输出及格式修复请求必须包含一个白名单模板，记录其实际消息位置；阶段选择和修复均正常计账。

文件创建失败在任何派发前终止；请求模板关联或候选落盘失败会撤销该隔离批次的后续派发，
不退款、不重试，也不改变产品的解析、格式修复、取消或异常行为。该旁路不注册到产品，
不启用真实模型入口；真实复测仍要求独立的有界 manifest、账本和用户授权。
