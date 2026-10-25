// Non-production P2 validation support. Never registered in the application.
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;

namespace FgoPet.Testing.Persona;

internal sealed record P2BatchPolicy(string BatchId, string FixtureFingerprint, int? MaxRequests, long CostCapMicros,
    DateTimeOffset ExpiresUtc, string LedgerPath)
{
    // Null explicitly removes the request-count ceiling only for an approved local subscription.
    public bool LocalSubscription { get; init; }
    // The subscription route is explicitly unpriced. Zero internal monetary debit is not a claim
    // of free service; Snapshot reports null. The cap counts gateway sends, not its upstream retries.
    public const string SubscriptionEndpoint = "http://127.0.0.1:8326/v1/chat/completions";
    public const string SubscriptionModel = "buddy/deepseek-v4.1-flash";
    public const int SubscriptionInputBytes = 32768;
    public string EffectiveEndpoint => LocalSubscription ? SubscriptionEndpoint : Endpoint;
    public string EffectiveModel => LocalSubscription ? SubscriptionModel : Model;
    public string Provider => LocalSubscription ? "custom-openai-compatible" : "deepseek";
    public int EffectiveContextTokens => LocalSubscription ? 131072 : ContextTokens;
    public long Reservation(int output) => LocalSubscription ? 0 : Reserve(output);
    public long Charge(ChatUsage usage) => LocalSubscription ? 0 : Cost(usage);
    // Frozen official peak CNY tariff, verified 2026-10-14. No cache discount is assumed.
    // One token costs InputMicros/OutputMicros millionths of CNY.
    public const int ContextTokens = 1_048_576;
    public const int OutputLimit = 2048;
    public const long InputMicros = 2;
    public const long OutputMicros = 8;
    public const string Endpoint = "https://api.deepseek.com/v1/chat/completions";
    public const string Model = "deepseek-flash";
    public const string PriceVersion = "deepseek-v4.1-flash-cny-20261014-peak";
    public static readonly DateTimeOffset PriceValidUntil = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { BatchId, FixtureFingerprint, MaxRequests, CostCapMicros, ExpiresUtc, LocalSubscription,
        Endpoint = EffectiveEndpoint, Model = EffectiveModel, Provider,
        Tariff = LocalSubscription ? "unpriced-user-authorized-subscription" : PriceVersion,
        ContextTokens = EffectiveContextTokens, OutputLimit, InputLimit = LocalSubscription ? SubscriptionInputBytes : (int?)null,
        InputMicros = LocalSubscription ? 0 : InputMicros, OutputMicros = LocalSubscription ? 0 : OutputMicros,
        Currency = LocalSubscription ? null : "CNY", LedgerIdentity = CanonicalPath(LedgerPath) }))));
    public static string CanonicalPath(string path) => Path.GetFullPath(path).ToUpperInvariant();
    public static long Reserve(int output) => checked(ContextTokens * InputMicros + output * OutputMicros);
    public static long Cost(ChatUsage usage) => checked(usage.InputTokens * InputMicros + usage.OutputTokens * OutputMicros);
    public void Validate()
    {
        if (!Guid.TryParseExact(BatchId, "N", out _) || FixtureFingerprint.Length != 64 ||
            !FixtureFingerprint.All(Uri.IsHexDigit) || (MaxRequests is null ? !LocalSubscription : MaxRequests is < 1 or > 20) ||
            (LocalSubscription ? CostCapMicros != 0 : CostCapMicros is <= 0 or > 3_000_000 || ExpiresUtc > PriceValidUntil))
            throw new P2BudgetException("P2_POLICY_INVALID");
    }
}
internal sealed record P2BatchSnapshot(int Requests, long? CommittedMicros, string? StopCode);
[JsonConverter(typeof(JsonStringEnumConverter<P2FailureReason>))]
internal enum P2FailureReason
{
    None, HttpStatus, Transport, Cancelled, Timeout, ResponseRead, ResponseLimit,
    UsageFrameInvalid, UsageMissingDone, UsageMissingFinish, UsageMissingFinal,
    UsageOutOfRange, UsageDecreased, UsageTotalMismatch, UsageZeroOutput,
    UsageAfterDone, UsageAfterFinish, UsageFinishUnsupported, UsageDuplicateProperty,
    LocalGuard, ApprovalExpired, ApprovalRevoked, Adapter, ConsumerStopped
}
internal sealed record P2RequestDiagnostic(DateTimeOffset At, string BatchHash, int Attempt,
    string Stage, string Status, P2FailureReason Reason, int? HttpStatus, P2ResponseDiagnostic? Response = null,
    P2FinalizeValidation? FinalizeRejection = null, string? HostTurnHash = null, P2PreDispatchRefusal? PreDispatchRefusal = null);
internal sealed record P2PreDispatchRefusal(int ReservedRequests, string Code);
internal sealed class P2BudgetException(string code, P2FailureReason reason = P2FailureReason.LocalGuard) : Exception(code)
{
    public string Code { get; } = code;
    public P2FailureReason Reason { get; } = reason;
}
internal sealed class P2BatchProvider : IChatProvider, IDisposable
{
    private readonly Ledger ledger;
    private readonly string? approval;
    private readonly ICredentialReader credentials;
    private readonly Func<HttpMessageHandler> transport;
    private P2BatchProvider(Ledger ledger, string? approval, ICredentialReader credentials, Func<HttpMessageHandler> transport)
    { this.ledger = ledger; this.approval = approval; this.credentials = credentials; this.transport = transport; }
    public Action<P2RequestDiagnostic>? Diagnostic { get; set; }
    public string ProviderId => ledger.Policy.Provider;
    public string ModelId => ledger.Policy.EffectiveModel;
    public P2BatchSnapshot Snapshot => ledger.Snapshot;
    public string ApprovalFingerprint => ledger.ApprovalFingerprint;
    public static P2BatchProvider CreateUnapproved(string path, P2BatchPolicy policy, ICredentialReader credentials,
        Func<HttpMessageHandler> transport, TimeProvider clock) => new(new(path, policy, clock, create: true), null, credentials, transport);
    public static P2BatchProvider Open(string path, P2BatchPolicy policy, string? approval, ICredentialReader credentials,
        Func<HttpMessageHandler> transport, TimeProvider clock) => new(new(path, policy, clock, create: false), approval, credentials, transport);

    // An immutable predecessor audit owns a read-only exclusive handle. Hashing and replay use
    // that same handle; it exposes neither credentials/transport nor a journal mutation method.
    public static AuditLease Audit(string path, P2BatchPolicy policy, string expectedHash)
    {
        if (expectedHash is not { Length: 64 } || expectedHash.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new P2BudgetException("P2_PREDECESSOR_CHANGED");
        return new(path, policy, expectedHash);
    }
    internal sealed class AuditLease : IDisposable
    {
        private readonly Ledger ledger;
        internal AuditLease(string path, P2BatchPolicy policy, string expectedHash) =>
            ledger = new(path, policy, TimeProvider.System, create: false, auditHash: expectedHash);
        public P2BatchSnapshot Snapshot => ledger.Snapshot;
        public void Dispose() => ledger.Dispose();
    }

    // A future approved runner must use this terminal handler. Injection is for offline tests only;
    // a retrying/custom production handler would invalidate the managed-send boundary.
    public static HttpMessageHandler CreateDirectTransport() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Frozen documentation metadata, not a live /models probe.
        return Task.FromResult<IReadOnlyList<ProviderModel>>([new(ModelId, contextWindowTokens: ledger.Policy.EffectiveContextTokens,
            maxOutputTokens: P2BatchPolicy.OutputLimit)]);
    }
    public void Revoke() => ledger.Revoke();
    public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken) =>
        StreamAsync(request, cancellationToken, null);

    internal async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, string? hostTurnHash)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var turnHash = P2ResponseDiagnostics.SafeTurnHash(hostTurnHash);
        var output = request.MaxOutputTokens ?? 0;
        try { ledger.Enter(approval, output); } // Approval and limits precede credential and transport access.
        catch (P2BudgetException error)
        {
            ObservePreDispatchRefusal(error, turnHash);
            throw; // No reservation, credential read, transport, or ledger.Exit for a rejected Enter.
        }
        var trace = new RequestDiagnostics(ledger, Diagnostic, cancellationToken, request, turnHash);
        try
        {
            using var guard = new DispatchGuard(ledger, approval, output, transport(), trace);
            using var client = new HttpClient(guard);
            var provider = new OpenAiCompatibleChatProvider(ProviderId,
                new(ledger.Policy.EffectiveEndpoint[..^"chat/completions".Length]), ModelId, credentials, client);
            await using var chunks = provider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (await NextAsync(chunks, trace, cancellationToken))
            {
                var chunk = chunks.Current;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    trace.ObserveChunk(chunk);
                    // Consumers break at IsComplete: settle before yielding it.
                    if (chunk.IsComplete)
                    {
                        trace.Stage = "settlement";
                        ledger.Settle(guard.FinalUsage ?? throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageMissingFinal));
                        // Stop local batches before yielding length, independently of diagnostic delivery.
                        if (ledger.Policy.LocalSubscription && chunk.FinishReason == "length") ledger.Revoke();
                        trace.Complete();
                    }
                }
                catch (Exception error) { trace.Fail(error); throw; }
                yield return chunk;
            }
        }
        finally
        {
            try { trace.End(cancellationToken); }
            finally { ledger.Exit(); }
        }
    }
    private static async ValueTask<bool> NextAsync(IAsyncEnumerator<ChatStreamChunk> chunks,
        RequestDiagnostics trace, CancellationToken token)
    {
        try { return await chunks.MoveNextAsync(); }
        catch (Exception error) { trace.Fail(error); throw; }
    }

    private void ObservePreDispatchRefusal(P2BudgetException error, string? turnHash)
    {
        try
        {
            var code = error.Code switch
            {
                "P2_REQUEST_LIMIT" or "P2_OUTPUT_LIMIT" or "P2_COST_LIMIT" or "P2_IN_FLIGHT" or
                "P2_APPROVAL_REQUIRED" or "P2_APPROVAL_EXPIRED" or "P2_APPROVAL_REVOKED" or "P2_USAGE_UNKNOWN" => error.Code,
                _ => "P2_LOCAL_GUARD"
            };
            // Attempt=0: Enter failed before any durable reservation or wire identity existed.
            Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ledger.Policy.BatchId))), 0,
                "pre-dispatch", "rejected", P2FailureReason.LocalGuard, null, HostTurnHash: turnHash,
                PreDispatchRefusal: new(ledger.Snapshot.Requests, code)));
        }
        catch { /* Observation must not replace the original guard exception or mutate its ledger. */ }
    }

    // Fixed categories only: never format exceptions, headers, URLs, requests or response bodies.
    private sealed class RequestDiagnostics(Ledger ledger, Action<P2RequestDiagnostic>? sink, CancellationToken callerToken,
        ChatRequest request, string? hostTurnHash)
    {
        private readonly string batchHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ledger.Policy.BatchId)));
        private readonly P2FinalizeDiagnostics? finalize = sink is null ? null : P2FinalizeDiagnostics.Create(request);
        private bool started, terminal;
        public string Stage { get; set; } = "adapter";
        public int? HttpStatus { get; set; }
        private P2ResponseDiagnostic? response;
        public void ObserveChunk(ChatStreamChunk chunk) => finalize?.Add(chunk);
        public void ObserveResponse(byte[] bytes)
        {
            if (sink is null) return;
            response = P2ResponseDiagnostics.ReadWire(bytes);
        }
        private void Emit(string status, P2FailureReason reason)
        {
            // Diagnostics cannot authorize, refund, retry, or mask the underlying outcome.
            try { sink?.Invoke(new(DateTimeOffset.UtcNow, batchHash, ledger.Snapshot.Requests, Stage, status, reason, HttpStatus, response,
                status == "completed" ? finalize?.Rejection : null, hostTurnHash)); }
            catch { /* An unavailable diagnostic sink does not alter the durable accounting boundary. */ }
        }
        public void Start() { started = true; Stage = "dispatch"; Emit("started", P2FailureReason.None); }
        public void Received() { Stage = "http-response"; Emit("received", P2FailureReason.None); }
        public void Complete() { terminal = true; Emit("completed", P2FailureReason.None); }
        public void Fail(P2FailureReason reason)
        {
            if (terminal) return;
            terminal = true;
            Emit("failed", reason);
        }
        public void Fail(Exception error) => Fail(error switch
        {
            P2BudgetException { Code: "P2_RESPONSE_LIMIT" } => P2FailureReason.ResponseLimit,
            P2BudgetException { Code: "P2_APPROVAL_EXPIRED" } => P2FailureReason.ApprovalExpired,
            P2BudgetException { Code: "P2_APPROVAL_REVOKED" } => P2FailureReason.ApprovalRevoked,
            P2BudgetException budget => budget.Reason,
            OperationCanceledException when callerToken.IsCancellationRequested => P2FailureReason.Cancelled,
            OperationCanceledException => P2FailureReason.Timeout,
            HttpRequestException => P2FailureReason.Transport,
            IOException when Stage == "response-read" => P2FailureReason.ResponseRead,
            IOException => P2FailureReason.Transport,
            _ => P2FailureReason.Adapter
        });
        public void End(CancellationToken token)
        {
            if (started && !terminal) Fail(token.IsCancellationRequested ? P2FailureReason.Cancelled : P2FailureReason.ConsumerStopped);
        }
    }
    public void Dispose() => ledger.Dispose();

    private sealed class DispatchGuard(Ledger ledger, string? approval, int output, HttpMessageHandler inner, RequestDiagnostics trace) : DelegatingHandler(inner)
    {
        public ChatUsage? FinalUsage { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != ledger.Policy.EffectiveEndpoint || request.Content is null)
                throw new P2BudgetException("P2_ROUTE_MISMATCH");
            var wire = await request.Content.ReadAsStringAsync(token);
            if (ledger.Policy.LocalSubscription && Encoding.UTF8.GetByteCount(wire) > P2BatchPolicy.SubscriptionInputBytes)
                throw new P2BudgetException("P2_INPUT_LIMIT");
            using (var body = JsonDocument.Parse(wire))
            {
                var root = body.RootElement;
                var allowed = new[] { "model", "messages", "stream", "max_tokens", "tools", "tool_choice", "thinking", "response_format" };
                if (root.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)) ||
                    root.GetProperty("model").GetString() != ledger.Policy.EffectiveModel || !root.GetProperty("stream").GetBoolean() ||
                    root.GetProperty("max_tokens").GetInt32() != output)
                    throw new P2BudgetException("P2_REQUEST_MISMATCH");
            }
            ledger.Reserve(approval, output); // Durable debit, then exactly one managed HTTP send.
            token.ThrowIfCancellationRequested();
            ledger.CheckDispatchApproval(approval); // Disk flush may have crossed the approval deadline.
            trace.Start();
            // A diagnostic sink may cross expiry or allow revocation/cancellation before dispatch.
            token.ThrowIfCancellationRequested();
            ledger.CheckDispatchApproval(approval);
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, token); }
            catch (Exception error) { trace.Fail(error); throw; }
            trace.HttpStatus = (int)response.StatusCode;
            trace.Received();
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    // Preserve the real adapter/parser, but buffer this bounded P2 response to audit the
                    // raw final usage (the production parser intentionally ignores malformed usage fields).
                    // This validation path does not measure incremental streaming UX or latency.
                    trace.Stage = "response-read";
                    var bytes = await ReadBoundedAsync(response.Content, token);
                    trace.ObserveResponse(bytes);
                    trace.Stage = "usage-validation";
                    FinalUsage = AuditUsage(bytes, output, ledger.Policy.EffectiveContextTokens);
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent(bytes);
                }
                else trace.Fail(P2FailureReason.HttpStatus);
                trace.Stage = "adapter";
                return response;
            }
            catch (Exception error) { trace.Fail(error); response.Dispose(); throw; }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var source = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(block, token)) != 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new P2BudgetException("P2_RESPONSE_LIMIT");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    private static ChatUsage AuditUsage(byte[] bytes, int output, int contextTokens)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            // Match the production StreamReader's initial UTF-8 BOM handling. Other encodings
            // remain rejected by strict UTF-8 decoding rather than silently skipping a frame.
            if (text.StartsWith('\uFEFF')) text = text[1..];
            using var reader = new StringReader(text);
            ChatUsage? previous = null, final = null;
            bool finished = false, done = false, emitted = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length > 512 * 1024) throw new P2BudgetException("P2_RESPONSE_LIMIT");
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (done) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageAfterDone);
                var data = line[5..].Trim();
                if (data == "[DONE]") { done = true; continue; }
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                RejectDuplicateProperties(root);
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1)
                    throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageFrameInvalid);
                foreach (var choice in choices.EnumerateArray())
                {
                    var hasDelta = choice.TryGetProperty("delta", out var delta) && delta.EnumerateObject().Any(p =>
                        p.Name is "content" or "reasoning_content" or "reasoning" or "tool_calls" &&
                        p.Value.ValueKind != JsonValueKind.Null && p.Value.ToString().Length > 0);
                    if (finished && hasDelta) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageAfterFinish);
                    emitted |= hasDelta;
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                    {
                        if (reason.GetString() is not ("stop" or "length" or "tool_calls")) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageFinishUnsupported);
                        finished = true;
                    }
                }
                final = null; // An ignored/empty/malformed final frame must not reuse earlier usage.
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
                {
                    var input = usage.GetProperty("prompt_tokens").GetInt32();
                    var generated = usage.GetProperty("completion_tokens").GetInt32();
                    if (input < 1 || input > contextTokens || generated < 0 || generated > output)
                        throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageOutOfRange);
                    if (previous is not null && (input < previous.InputTokens || generated < previous.OutputTokens))
                        throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageDecreased);
                    if (usage.TryGetProperty("total_tokens", out var total) && total.GetInt32() != input + generated)
                        throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageTotalMismatch);
                    previous = new(input, generated);
                    if (finished) final = previous;
                }
            }
            if (!done) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageMissingDone);
            if (!finished) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageMissingFinish);
            if (final is null) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageMissingFinal);
            if (emitted && final.OutputTokens == 0) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageZeroOutput);
            return final;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException or DecoderFallbackException)
        { throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageFrameInvalid); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new P2BudgetException("P2_USAGE_UNKNOWN", P2FailureReason.UsageDuplicateProperty);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed class Ledger : IDisposable
    {
        private sealed record Entry(int Sequence, string Kind, long At, int Attempt, int Output, long Micros,
            int? InputTokens = null, int? OutputTokens = null, string? Policy = null, string? Nonce = null);
        private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        private readonly object gate = new();
        private readonly P2BatchPolicy policy;
        private readonly TimeProvider clock;
        private readonly FileStream file;
        private readonly bool readOnly;
        private int sequence, requests, pendingOutput;
        private long committed, lastAt;
        private bool active, disposed;
        private string? stopCode;
        private string nonce = string.Empty;
        public P2BatchPolicy Policy => policy;
        public string ApprovalFingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy.Fingerprint + ":" + nonce)));
        public P2BatchSnapshot Snapshot { get { lock (gate) return new(requests, policy.LocalSubscription ? null : committed, stopCode); } }
        public Ledger(string path, P2BatchPolicy policy, TimeProvider clock, bool create, string? auditHash = null)
        {
            policy.Validate();
            if (P2BatchPolicy.CanonicalPath(path) != P2BatchPolicy.CanonicalPath(policy.LedgerPath))
                throw new P2BudgetException("P2_LEDGER_PATH");
            this.policy = policy;
            this.clock = clock;
            readOnly = auditHash is not null;
            if (readOnly && create) throw new P2BudgetException("P2_LEDGER_READ_ONLY");
            file = new(path, create ? FileMode.CreateNew : FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (readOnly)
                {
                    if (file.Length == 0 || (policy.MaxRequests is not null && file.Length > 65536) ||
                        Convert.ToHexString(SHA256.HashData(file)) != auditHash)
                        throw new P2BudgetException("P2_PREDECESSOR_CHANGED");
                    file.Position = 0;
                }
                if (create) Append(new(0, "created", Now(), 0, 0, 0, Policy: policy.Fingerprint, Nonce: Guid.NewGuid().ToString("N")));
                else
                {
                    if (file.Length == 0 || (policy.MaxRequests is not null && file.Length > 65536))
                        throw new P2BudgetException("P2_LEDGER_INVALID");
                    using var reader = new StreamReader(file, new UTF8Encoding(false, true), leaveOpen: true);
                    // Replay incrementally: uncapped batches may exceed the old total-file bound,
                    // but a malformed line must never cause an unbounded string allocation.
                    var line = new StringBuilder();
                    int character;
                    while ((character = reader.Read()) >= 0)
                    {
                        if (character == '\n')
                        {
                            if (line.Length != 0)
                                Apply(JsonSerializer.Deserialize<Entry>(line.ToString(), Json) ?? throw new P2BudgetException("P2_LEDGER_INVALID"));
                            line.Clear();
                        }
                        else
                        {
                            if (line.Length >= 2048) throw new P2BudgetException("P2_LEDGER_INVALID");
                            line.Append((char)character);
                        }
                    }
                    if (line.Length != 0) throw new P2BudgetException("P2_LEDGER_INVALID");
                    if (pendingOutput > 0) stopCode = "P2_USAGE_UNKNOWN"; // Crash/restart never refunds or replays.
                    file.Seek(0, SeekOrigin.End);
                }
            }
            catch { file.Dispose(); throw; }
        }
        private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
        private void CheckApproval(string? approval)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (approval != ApprovalFingerprint) throw new P2BudgetException("P2_APPROVAL_REQUIRED");
            if (stopCode is not null) throw new P2BudgetException(stopCode);
            var now = clock.GetUtcNow();
            if (now >= policy.ExpiresUtc || !policy.LocalSubscription && now >= P2BatchPolicy.PriceValidUntil || now.ToUnixTimeMilliseconds() < lastAt)
                throw new P2BudgetException("P2_APPROVAL_EXPIRED");
        }
        private void Check(string? approval, int output)
        {
            CheckApproval(approval);
            if (output is < 1 or > P2BatchPolicy.OutputLimit) throw new P2BudgetException("P2_OUTPUT_LIMIT");
            if (policy.MaxRequests is int limit && requests >= limit) throw new P2BudgetException("P2_REQUEST_LIMIT");
            if (committed + policy.Reservation(output) > policy.CostCapMicros) throw new P2BudgetException("P2_COST_LIMIT");
        }
        public void Enter(string? approval, int output)
        {
            lock (gate)
            {
                Check(approval, output);
                if (active) throw new P2BudgetException("P2_IN_FLIGHT");
                active = true;
            }
        }
        public void Reserve(string? approval, int output)
        {
            lock (gate)
            {
                Check(approval, output); // Recheck expiry/revocation after asynchronous credential access.
                if (!active || pendingOutput != 0) throw new P2BudgetException("P2_IN_FLIGHT");
                Append(new(sequence, "reserved", Now(), requests + 1, output, policy.Reservation(output)));
            }
        }
        public void CheckDispatchApproval(string? approval)
        {
            lock (gate)
            {
                CheckApproval(approval);
                if (!active || pendingOutput == 0) throw new P2BudgetException("P2_IN_FLIGHT");
            }
        }
        public void Settle(ChatUsage usage)
        {
            lock (gate)
            {
                if (stopCode is not null) throw new P2BudgetException(stopCode);
                Append(new(sequence, "settled", Now(), requests, pendingOutput, policy.Charge(usage), usage.InputTokens, usage.OutputTokens));
            }
        }
        public void Exit()
        {
            lock (gate)
            {
                if (!disposed && pendingOutput != 0 && stopCode is null)
                    Append(new(sequence, "unknown", Now(), requests, pendingOutput, 0));
                active = false;
            }
        }
        public void Revoke()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (stopCode is null) Append(new(sequence, "revoked", Now(), requests, pendingOutput, 0));
            }
        }
        private void Append(Entry entry)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (readOnly) throw new P2BudgetException("P2_LEDGER_READ_ONLY");
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                Apply(entry);
            }
            catch { stopCode = "P2_LEDGER_IO"; throw; }
        }
        private void Apply(Entry entry)
        {
            if (entry.Sequence != sequence || entry.At < lastAt) throw new P2BudgetException("P2_LEDGER_INVALID");
            if (sequence == 0)
            {
                if (entry.Kind != "created" || entry.Policy != policy.Fingerprint || entry.Attempt != 0 || entry.Micros != 0 ||
                    !Guid.TryParseExact(entry.Nonce, "N", out _))
                    throw new P2BudgetException("P2_LEDGER_INVALID");
                nonce = entry.Nonce!;
            }
            else if (entry.Kind == "reserved" && stopCode is null && pendingOutput == 0 && entry.Attempt == requests + 1 &&
                entry.Output is >= 1 and <= P2BatchPolicy.OutputLimit && entry.Micros == policy.Reservation(entry.Output) &&
                (policy.MaxRequests is null || requests < policy.MaxRequests) && committed + entry.Micros <= policy.CostCapMicros)
            { requests++; committed += entry.Micros; pendingOutput = entry.Output; }
            else if (entry.Kind == "settled" && stopCode is null && pendingOutput > 0 && entry.Attempt == requests &&
                entry.Output == pendingOutput && entry.InputTokens >= 1 && entry.InputTokens <= policy.EffectiveContextTokens &&
                entry.OutputTokens is >= 0 && entry.OutputTokens <= pendingOutput &&
                entry.Micros == policy.Charge(new(entry.InputTokens.Value, entry.OutputTokens.Value)))
            { committed += entry.Micros - policy.Reservation(pendingOutput); pendingOutput = 0; }
            else if (entry.Kind is "unknown" or "revoked" && entry.Attempt == requests && entry.Micros == 0)
                stopCode = entry.Kind == "revoked" ? "P2_APPROVAL_REVOKED" : "P2_USAGE_UNKNOWN";
            else throw new P2BudgetException("P2_LEDGER_INVALID");
            sequence++;
            lastAt = entry.At;
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; file.Dispose(); } }
    }
}

// Request-local diagnostic context only; never placed in the model's messages or HTTP payload.
internal sealed class P2TurnScopedProvider(P2BatchProvider inner, Func<string?> currentTurnHash) : IChatProvider
{
    public string ProviderId => inner.ProviderId;
    public string ModelId => inner.ModelId;
    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
    public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken token) =>
        inner.StreamAsync(request, token, ReadTurnHash());
    private string? ReadTurnHash()
    {
        try { return currentTurnHash(); }
        catch { return null; } // A missing diagnostic context cannot fail or redirect the request.
    }
}
