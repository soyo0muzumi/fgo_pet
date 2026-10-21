using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Testing.Persona;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class P2BatchProviderTests
{
    [Theory]
    [InlineData("http", nameof(P2FailureReason.HttpStatus), 503)]
    [InlineData("transport", nameof(P2FailureReason.Transport), null)]
    [InlineData("cancel", nameof(P2FailureReason.Cancelled), null)]
    [InlineData("timeout", nameof(P2FailureReason.Timeout), null)]
    [InlineData("read", nameof(P2FailureReason.ResponseRead), 200)]
    [InlineData("invalid", nameof(P2FailureReason.UsageFrameInvalid), 200)]
    [InlineData("done", nameof(P2FailureReason.UsageMissingDone), 200)]
    [InlineData("finish", nameof(P2FailureReason.UsageMissingFinish), 200)]
    [InlineData("usage", nameof(P2FailureReason.UsageMissingFinal), 200)]
    [InlineData("range", nameof(P2FailureReason.UsageOutOfRange), 200)]
    [InlineData("decreased", nameof(P2FailureReason.UsageDecreased), 200)]
    [InlineData("total", nameof(P2FailureReason.UsageTotalMismatch), 200)]
    [InlineData("zero", nameof(P2FailureReason.UsageZeroOutput), 200)]
    [InlineData("after-done", nameof(P2FailureReason.UsageAfterDone), 200)]
    [InlineData("after-finish", nameof(P2FailureReason.UsageAfterFinish), 200)]
    [InlineData("unsupported", nameof(P2FailureReason.UsageFinishUnsupported), 200)]
    [InlineData("duplicate", nameof(P2FailureReason.UsageDuplicateProperty), 200)]
    public async Task Safe_diagnostics_classify_unsettled_requests_without_private_content(string scenario, string reason, int? status)
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        using var cancellation = new CancellationTokenSource();
        var events = new List<P2RequestDiagnostic>();
        host.Batch.Diagnostic = events.Add;
        var valid = "data: {\"choices\":[{\"delta\":{\"content\":\"private-response\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100}}\n\ndata: [DONE]\n\n";
        host.Respond = (_, _) =>
        {
            if (scenario == "transport") throw new HttpRequestException("private-url/private-key");
            if (scenario == "timeout") throw new TaskCanceledException("private-timeout");
            if (scenario == "cancel") { cancellation.Cancel(); throw new OperationCanceledException("private-cancel", cancellation.Token); }
            if (scenario == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("private-response/private-key") });
            if (scenario == "read") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new BrokenDiagnosticStream()) });
            var wire = scenario switch
            {
                "invalid" => "data: private-invalid-json\n\n",
                "done" => valid.Replace("data: [DONE]\n\n", ""),
                "finish" => valid.Replace("\"stop\"", "null"),
                "usage" => valid.Replace(",\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100}", ""),
                "range" => valid.Replace("\"completion_tokens\":100", "\"completion_tokens\":2049"),
                "decreased" => "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1001,\"completion_tokens\":100}}\n\n" + valid,
                "total" => valid.Replace("\"completion_tokens\":100", "\"completion_tokens\":100,\"total_tokens\":2"),
                "zero" => valid.Replace("\"completion_tokens\":100", "\"completion_tokens\":0"),
                "after-done" => valid + "data: {}\n\n",
                "after-finish" => valid.Replace("data: [DONE]", "data: {\"choices\":[{\"delta\":{\"content\":\"private-extra\"}}]}\n\ndata: [DONE]"),
                "unsupported" => valid.Replace("\"stop\"", "\"private-finish\""),
                "duplicate" => valid.Replace("\"prompt_tokens\":1000", "\"prompt_tokens\":1000,\"prompt_tokens\":1000"),
                _ => throw new InvalidOperationException("invalid fixture")
            };
            return Task.FromResult(Response(wire));
        };
        await Assert.ThrowsAnyAsync<Exception>(() => host.Send(token: cancellation.Token));
        Assert.Equal(1, host.Sends);
        Assert.Equal("P2_USAGE_UNKNOWN", host.Batch.Snapshot.StopCode);
        var failure = Assert.Single(events, item => item.Status == "failed");
        Assert.Equal(reason, failure.Reason.ToString());
        Assert.Equal(status, failure.HttpStatus);
        Assert.Equal(1, failure.Attempt);
        Assert.Equal(64, failure.BatchHash.Length);
        Assert.Contains(events, item => item.Stage == "dispatch" && item.Status == "started");
        var safe = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("private-", safe);
        Assert.DoesNotContain("synthetic-key", safe);
        Assert.DoesNotContain(host.DirectoryPath, safe);
        Assert.DoesNotContain(host.Policy.BatchId, safe);
        host.Reopen();
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
    }

    [Fact]
    public async Task Safe_diagnostics_record_settlement_and_do_not_change_success_when_sink_fails()
    {
        using var host = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        host.Batch.Diagnostic = events.Add;
        await host.Send();
        Assert.Contains(events, item => item.Stage == "settlement" && item.Status == "completed" && item.Reason == P2FailureReason.None);
        Assert.DoesNotContain(events, item => item.Status == "failed");
        host.Batch.Diagnostic = _ => throw new IOException("private-sink");
        await host.Send();
        Assert.Equal(2, host.Sends);
        Assert.Null(host.Batch.Snapshot.StopCode);
    }

    [Theory]
    [InlineData("missing", 203)]
    [InlineData("missing", 205)]
    [InlineData("delayed", 203)]
    [InlineData("delayed", 205)]
    [InlineData("half-line", 203)]
    [InlineData("half-line", 205)]
    public async Task Subscription_length_stops_before_second_http_without_complete_gateway_diagnostic(string diagnostic, int output)
    {
        using var host = new BatchFixture(subscription: true);
        var path = Path.Combine(host.DirectoryPath, "gateway.jsonl");
        File.WriteAllText(path, ""); // Existing log, but no diagnostic for this request yet.
        try
        {
            host.Respond = (_, _) =>
            {
                if (diagnostic == "half-line")
                    File.WriteAllText(path, "{\"stage\":\"gateway-finish\",\"openAiFinish\":\"len");
                return Task.FromResult(Response("data: " + JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = "fixture" }, finish_reason = "length" } },
                    usage = new { prompt_tokens = 1000, completion_tokens = output },
                }) + "\n\ndata: [DONE]\n\n"));
            };
            var completed = false;
            await foreach (var chunk in host.Batch.StreamAsync(Request(), default))
                if (chunk.IsComplete)
                {
                    Assert.Equal("length", chunk.FinishReason);
                    completed = true;
                    break; // The stop must already be durable when the consumer ends iteration.
                }
            Assert.True(completed);
            var credentials = host.Credentials.Reads;
            var transports = host.TransportCreations;
            var refused = await Record.ExceptionAsync(() => host.Send());

            Assert.Equal(1, host.Sends);
            Assert.Equal("P2_APPROVAL_REVOKED", Assert.IsType<P2BudgetException>(refused).Code);
            Assert.Equal(credentials, host.Credentials.Reads);
            Assert.Equal(transports, host.TransportCreations);
            Assert.Equal(1, host.Batch.Snapshot.Requests);
            Assert.Equal(20, host.Policy.MaxRequests);
            if (diagnostic == "delayed")
                File.WriteAllText(path, "{\"stage\":\"gateway-finish\",\"openAiFinish\":\"length\"}\n");
            else if (diagnostic == "missing") Assert.Empty(File.ReadAllText(path));
            else Assert.DoesNotContain('\n', File.ReadAllText(path));

            host.Batch.Dispose();
            var entries = File.ReadAllLines(host.LedgerPath).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Equal(new[] { "created", "reserved", "settled", "revoked" },
                entries.Select(entry => entry.GetProperty("Kind").GetString()));
            Assert.Equal(2048, entries[1].GetProperty("Output").GetInt32());
            Assert.Equal(output, entries[2].GetProperty("OutputTokens").GetInt32());
            host.Reopen();
            Assert.Equal("P2_APPROVAL_REVOKED", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
            Assert.Equal(1, host.Sends);

            var evidence = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.AppendAllText(Path.Combine(evidence, "wire-length-stop.jsonl"), JsonSerializer.Serialize(new
                {
                    AtUtc = DateTimeOffset.UtcNow, Diagnostic = diagnostic, OutputTokens = output, ModelCalls = 0,
                    SyntheticHttpSends = host.Sends, Reserved = 1, Settled = 1, Revoked = 1, Unknown = 0,
                    SecondHttpBlocked = true, ReopenBlocked = true, OutputLimit = 2048,
                }) + "\n");
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(203)]
    [InlineData(205)]
    public async Task Subscription_length_stops_even_when_safe_diagnostic_sink_throws(int output)
    {
        using var host = new BatchFixture(subscription: true);
        host.Batch.Diagnostic = _ => throw new IOException("synthetic-sink-unavailable");
        host.Respond = (_, _) => Task.FromResult(Response("data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = "fixture" }, finish_reason = "length" } },
            usage = new { prompt_tokens = 1000, completion_tokens = output },
        }) + "\n\ndata: [DONE]\n\n"));
        await host.Send();
        var refused = await Record.ExceptionAsync(() => host.Send());
        Assert.Equal(1, host.Sends);
        Assert.Equal("P2_APPROVAL_REVOKED", Assert.IsType<P2BudgetException>(refused).Code);
        Assert.Equal(1, host.Batch.Snapshot.Requests);
    }

    [Theory]
    [InlineData("usage")]
    [InlineData("done")]
    public async Task Subscription_length_with_unverified_usage_keeps_unknown_reservation(string missing)
    {
        using var host = new BatchFixture(subscription: true);
        var wire = "data: {\"choices\":[{\"delta\":{\"content\":\"fixture\"},\"finish_reason\":\"length\"}]," +
            "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":203}}\n\ndata: [DONE]\n\n";
        wire = missing == "usage" ? wire.Replace(",\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":203}", "")
            : wire.Replace("data: [DONE]\n\n", "");
        host.Respond = (_, _) => Task.FromResult(Response(wire));
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
        host.Batch.Dispose();
        var kinds = File.ReadAllLines(host.LedgerPath).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("Kind").GetString()).ToArray();
        Assert.Equal(new[] { "created", "reserved", "unknown" }, kinds);
    }

    [Fact]
    public async Task Safe_diagnostics_classify_consumer_abandonment_without_refund_or_retry()
    {
        using var host = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        host.Batch.Diagnostic = events.Add;
        await foreach (var chunk in host.Batch.StreamAsync(Request(), default)) { Assert.False(chunk.IsComplete); break; }
        Assert.Equal(P2FailureReason.ConsumerStopped, Assert.Single(events, item => item.Status == "failed").Reason);
        Assert.Equal("P2_USAGE_UNKNOWN", host.Batch.Snapshot.StopCode);
        Assert.Equal(1, host.Sends);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("revoke")]
    [InlineData("cancel")]
    public async Task Safe_diagnostic_callback_cannot_dispatch_after_authorization_changes(string scenario)
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        using var cancellation = new CancellationTokenSource();
        var events = new List<P2RequestDiagnostic>();
        host.Batch.Diagnostic = item =>
        {
            events.Add(item);
            if (item.Stage != "dispatch" || item.Status != "started") return;
            if (scenario == "expiry") host.Clock.Advance(TimeSpan.FromHours(2));
            if (scenario == "revoke") host.Batch.Revoke();
            if (scenario == "cancel") cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<Exception>(() => host.Send(token: cancellation.Token));
        Assert.Equal(0, host.Sends);
        Assert.Equal(1, host.Batch.Snapshot.Requests);
        Assert.NotNull(host.Batch.Snapshot.StopCode);
        Assert.Single(events, item => item.Status == "failed");
        host.Reopen();
        await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
        Assert.Equal(0, host.Sends);
    }

    private sealed class BrokenDiagnosticStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private-response-read"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    public void Predecessor_audit_rejects_missing_hash_without_downgrading_to_read_write_open(string? hash)
    {
        using var host = new BatchFixture(subscription: true);
        // The existing writer holds the file exclusively; an attempt to open it would throw IO.
        Assert.Equal("P2_PREDECESSOR_CHANGED", Assert.Throws<P2BudgetException>(() => P2BatchProvider.Audit(host.LedgerPath, host.Policy, hash!)).Code);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.TransportCreations);
    }

    [Fact]
    public async Task Predecessor_audit_replays_three_settled_requests_with_read_only_exclusive_lock()
    {
        using var host = new BatchFixture(subscription: true);
        for (var index = 0; index < 3; index++) await host.Send();
        host.Batch.Dispose();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host.LedgerPath)));
        using (var audit = P2BatchProvider.Audit(host.LedgerPath, host.Policy, hash))
        {
            Assert.Equal(3, audit.Snapshot.Requests);
            Assert.Null(audit.Snapshot.StopCode);
            Assert.Throws<IOException>(() => new FileStream(host.LedgerPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.Throws<IOException>(() => P2BatchProvider.Audit(host.LedgerPath, host.Policy, hash));
        }
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host.LedgerPath))));
        Assert.Equal("P2_PREDECESSOR_CHANGED", Assert.Throws<P2BudgetException>(() => P2BatchProvider.Audit(host.LedgerPath, host.Policy, new string('0', 64))).Code);
        Assert.Equal(3, host.Credentials.Reads);
        Assert.Equal(3, host.TransportCreations);
    }

    [Fact]
    public async Task Subscription_uses_only_saved_loopback_route_and_keeps_twenty_request_limit_after_restart()
    {
        using var host = new BatchFixture(subscription: true);
        host.Respond = (request, _) =>
        {
            Assert.Equal("http://127.0.0.1:8326/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("buddy/deepseek-v4.1-flash", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
            return Task.FromResult(CompleteResponse());
        };
        for (var index = 0; index < 20; index++) await host.Send();
        Assert.Equal("fgo-pet/provider/custom-openai-compatible", host.Credentials.LastTarget);
        Assert.Null(host.Batch.Snapshot.CommittedMicros); // Unknown subscription billing is never reported as free.
        host.Reopen();
        Assert.Equal("P2_REQUEST_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(20, host.Sends);
        Assert.Equal(20, host.Batch.Snapshot.Requests);
    }

    [Fact]
    public async Task Uncapped_subscription_counts_beyond_twenty_and_survives_reopen()
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        for (var index = 0; index < 21; index++) await host.Send();
        host.Reopen();
        await host.Send();
        Assert.Equal(22, host.Sends);
        Assert.Equal(22, host.Batch.Snapshot.Requests);
        Assert.Null(host.Batch.Snapshot.CommittedMicros);
        Assert.Null(host.Batch.Snapshot.StopCode);
        Assert.NotEqual(host.Policy.Fingerprint, (host.Policy with { MaxRequests = 20 }).Fingerprint);
        host.Batch.Dispose();
        Assert.Equal("P2_LEDGER_INVALID", Assert.Throws<P2BudgetException>(() =>
            P2BatchProvider.Open(host.LedgerPath, host.Policy with { MaxRequests = 20 }, host.Approval,
                host.Credentials, () => throw new InvalidOperationException("NO_TRANSPORT"), host.Clock)).Code);
    }

    [Fact]
    public async Task Uncapped_ledger_reopens_and_audits_above_old_size_limit()
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        for (var index = 0; index < 500; index++) await host.Send();
        host.Batch.Dispose();
        Assert.True(new FileInfo(host.LedgerPath).Length > 65536);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host.LedgerPath)));
        using (var audit = P2BatchProvider.Audit(host.LedgerPath, host.Policy, hash))
            Assert.Equal(500, audit.Snapshot.Requests);
        host.Reopen();
        await host.Send();
        Assert.Equal(501, host.Sends);
        Assert.Equal(501, host.Batch.Snapshot.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uncapped_ledger_rejects_oversized_or_unterminated_entry(bool oversized)
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        host.Batch.Dispose();
        File.AppendAllText(host.LedgerPath, oversized ? new string(' ', 2049) + "\n" : "{}");
        Assert.Equal("P2_LEDGER_INVALID", Assert.Throws<P2BudgetException>(() =>
            P2BatchProvider.Open(host.LedgerPath, host.Policy, host.Approval,
                host.Credentials, () => throw new InvalidOperationException("NO_TRANSPORT"), host.Clock)).Code);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.Sends);
    }

    [Fact]
    public void Uncapped_count_is_rejected_for_official_billed_route()
    {
        using var host = new BatchFixture();
        Assert.Equal("P2_POLICY_INVALID", Assert.Throws<P2BudgetException>(() =>
            (host.Policy with { MaxRequests = null }).Validate()).Code);
    }

    [Theory]
    [InlineData("expiry", "P2_APPROVAL_EXPIRED")]
    [InlineData("revoked", "P2_APPROVAL_REVOKED")]
    [InlineData("unknown", "P2_USAGE_UNKNOWN")]
    public async Task Uncapped_subscription_still_stops_on_expiry_revocation_or_unknown_usage(string reason, string code)
    {
        using var host = new BatchFixture(subscription: true, maxRequests: null);
        await host.Send();
        if (reason == "expiry") host.Clock.Advance(TimeSpan.FromHours(2));
        if (reason == "revoked") host.Batch.Revoke();
        if (reason == "unknown")
        {
            host.Respond = (_, _) => Task.FromResult(Response("data: [DONE]\n\n"));
            Assert.Equal(code, (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        }
        host.Reopen();
        var sends = host.Sends;
        Assert.Equal(code, (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(sends, host.Sends);
    }

    [Fact]
    public async Task Subscription_rejects_oversized_wire_input_before_dispatch()
    {
        using var host = new BatchFixture(subscription: true);
        var request = new ChatRequest("fixture", "conversation", [new(ChatMessageRole.User, new string('中', 11000))], maxOutputTokens: 2048);
        Assert.Equal("P2_INPUT_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send(request))).Code);
        Assert.Equal(0, host.Sends);
        Assert.Equal(0, host.Batch.Snapshot.Requests);
    }

    [Fact]
    public async Task Subscription_unknown_usage_is_counted_and_stops_retries()
    {
        using var host = new BatchFixture(subscription: true);
        host.Respond = (_, _) => Task.FromResult(Response("data: [DONE]\n\n"));
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        host.Reopen();
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
        Assert.Equal(1, host.Batch.Snapshot.Requests);
        Assert.Null(host.Batch.Snapshot.CommittedMicros);
    }

    [Fact]
    public void Subscription_policy_does_not_inherit_official_tariff_or_approval()
    {
        using var host = new BatchFixture();
        var subscription = host.Policy with { LocalSubscription = true, CostCapMicros = 0,
            ExpiresUtc = P2BatchPolicy.PriceValidUntil.AddHours(1) };
        subscription.Validate();
        Assert.NotEqual(host.Policy.Fingerprint, subscription.Fingerprint);
        Assert.Throws<P2BudgetException>(() => (subscription with { LocalSubscription = false }).Validate());
        Assert.Throws<P2BudgetException>(() => (subscription with { MaxRequests = 21 }).Validate());
        Assert.Throws<P2BudgetException>(() => (subscription with { CostCapMicros = 3_000_000 }).Validate());
    }

    [Fact]
    public async Task Unapproved_batch_never_reads_credentials_or_creates_transport()
    {
        using var host = new BatchFixture(approved: false);
        var error = await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
        Assert.Equal("P2_APPROVAL_REQUIRED", error.Code);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.TransportCreations);
    }

    [Fact]
    public async Task Twenty_requests_are_allowed_but_twenty_first_never_reaches_transport()
    {
        using var host = new BatchFixture();
        for (var index = 0; index < 20; index++) await host.Send();
        var error = await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
        Assert.Equal("P2_REQUEST_LIMIT", error.Code);
        Assert.Equal(20, host.Sends);
        Assert.Equal(20, host.Batch.Snapshot.Requests);
        Assert.Equal(56_000, host.Batch.Snapshot.CommittedMicros);
    }

    [Fact]
    public async Task Cost_is_reserved_before_dispatch_and_settled_only_from_complete_usage()
    {
        using var host = new BatchFixture(capMicros: 2_114_000);
        host.OnSend = () => Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        await host.Send();
        Assert.Equal(2_800, host.Batch.Snapshot.CommittedMicros);
        var error = await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
        Assert.Equal("P2_COST_LIMIT", error.Code);
        Assert.Equal(1, host.Sends);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2049)]
    public async Task Missing_or_oversized_output_cap_is_denied_before_credentials(int? output)
    {
        using var host = new BatchFixture();
        var error = await Assert.ThrowsAsync<P2BudgetException>(() => host.Send(Request(output)));
        Assert.Equal("P2_OUTPUT_LIMIT", error.Code);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.TransportCreations);
    }

    [Fact]
    public async Task Persisted_limit_survives_restart_and_restart_needs_approval_again()
    {
        using var host = new BatchFixture();
        for (var index = 0; index < 20; index++) await host.Send();
        host.Reopen(approved: false);
        Assert.Equal("P2_APPROVAL_REQUIRED", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        host.Reopen();
        Assert.Equal(20, host.Batch.Snapshot.Requests);
        Assert.Equal(56_000, host.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_REQUEST_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(20, host.Sends);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("clock-rollback")]
    [InlineData("revoked")]
    public async Task Expiry_rollback_and_revocation_deny_new_dispatch(string mode)
    {
        using var host = new BatchFixture();
        if (mode == "expired") host.Clock.Advance(TimeSpan.FromHours(1));
        if (mode == "clock-rollback")
        {
            host.Batch.Dispose();
            File.WriteAllText(host.LedgerPath, File.ReadAllText(host.LedgerPath).Replace(
                host.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(), host.Clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds().ToString()));
            host.Reopen(); // The persisted clock is ahead of the current clock.
        }
        if (mode == "revoked") { host.Batch.Revoke(); host.Reopen(); }
        var error = await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
        Assert.Equal(mode == "revoked" ? "P2_APPROVAL_REVOKED" : "P2_APPROVAL_EXPIRED", error.Code);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.Sends);
    }

    [Fact]
    public async Task Approval_is_rechecked_after_awaiting_credentials()
    {
        using var host = new BatchFixture();
        host.Credentials.OnRead = () => host.Batch.Revoke();
        Assert.Equal("P2_APPROVAL_REVOKED", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(0, host.Sends);
        Assert.Equal(0, host.Batch.Snapshot.Requests);
    }

    [Fact]
    public async Task Expiry_during_durable_reservation_prevents_the_following_http_send()
    {
        using var host = new BatchFixture();
        host.Batch.Dispose();
        var headerLength = new FileInfo(host.LedgerPath).Length;
        var clock = new ObservedClock(() => new FileInfo(host.LedgerPath).Length > headerLength
            ? host.Policy.ExpiresUtc : host.Clock.GetUtcNow());
        var sends = 0;
        using var batch = P2BatchProvider.Open(host.LedgerPath, host.Policy, host.Approval, host.Credentials,
            () => new FixtureHandler((_, _) => { sends++; return Task.FromResult(CompleteResponse()); }), clock);
        Assert.Equal("P2_APPROVAL_EXPIRED", (await Assert.ThrowsAsync<P2BudgetException>(() => Consume(batch, Request()))).Code);
        Assert.Equal(0, sends);
        Assert.Equal(1, batch.Snapshot.Requests);
        Assert.Equal(2_113_536, batch.Snapshot.CommittedMicros);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_spend_an_inflight_reservation()
    {
        using var host = new BatchFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Respond = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return CompleteResponse(); };
        var first = host.Send();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<P2BudgetException>(() => host.Send());
            Assert.Equal(1, host.Sends);
            Assert.Equal(1, host.Credentials.Reads);
            Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        }
        finally { release.SetResult(); }
        await first;
        Assert.Equal(2_800, host.Batch.Snapshot.CommittedMicros);
    }

    [Fact]
    public async Task Consumers_that_break_on_terminal_observe_settlement_before_breaking()
    {
        using var host = new BatchFixture();
        await foreach (var chunk in host.Batch.StreamAsync(Request(), default))
            if (chunk.IsComplete)
            {
                Assert.Equal(2_800, host.Batch.Snapshot.CommittedMicros);
                break;
            }
        await host.Send();
        Assert.Equal(2, host.Sends);
    }

    [Fact]
    public async Task Early_consumer_disposal_keeps_full_debit_and_stops_the_batch()
    {
        using var host = new BatchFixture();
        await foreach (var _ in host.Batch.StreamAsync(Request(), default)) break;
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        host.Reopen();
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_spends_nothing_but_after_dispatch_keeps_full_debit()
    {
        using var host = new BatchFixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Send(token: canceled.Token));
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.Sends);
        using var during = new CancellationTokenSource();
        host.Respond = (_, token) => { during.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(CompleteResponse()); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Send(token: during.Token));
        Assert.Equal(1, host.Sends);
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_USAGE_UNKNOWN", host.Batch.Snapshot.StopCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    [InlineData(0)]
    public async Task Failed_or_uncertain_transport_is_never_refunded_or_retried_automatically(int status)
    {
        using var host = new BatchFixture();
        host.Respond = (_, _) => status == 0 ? throw new HttpRequestException("fixture") : Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("{\"error\":{\"message\":\"fixture\"}}") });
        await Assert.ThrowsAsync<ProviderRequestException>(() => host.Send());
        Assert.Equal(1, host.Batch.Snapshot.Requests);
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        host.Reopen();
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
    }

    [Theory]
    [InlineData("no-done")]
    [InlineData("no-usage")]
    [InlineData("early-usage")]
    [InlineData("malformed-final-usage")]
    [InlineData("negative")]
    [InlineData("zero-input")]
    [InlineData("output-over")]
    [InlineData("input-over")]
    [InlineData("total-mismatch")]
    [InlineData("decreasing")]
    [InlineData("after-finish")]
    [InlineData("uppercase-after-finish")]
    [InlineData("after-done")]
    [InlineData("duplicate-done")]
    [InlineData("duplicate-usage")]
    [InlineData("duplicate-completion")]
    [InlineData("bom-decreasing")]
    [InlineData("bom-duplicate")]
    [InlineData("reasoning-alias-after-finish")]
    [InlineData("malformed-json")]
    public async Task Unknown_or_nonfinal_usage_never_releases_budget(string variant)
    {
        using var host = new BatchFixture();
        const string usage = "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100}";
        var frame = "data: {\"choices\":[{\"delta\":{\"content\":\"fixture\"},\"finish_reason\":\"stop\"}]," + usage + "}\n\n";
        var done = "data: [DONE]\n\n";
        var body = variant switch
        {
            "no-done" => frame,
            "no-usage" => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + done,
            "early-usage" => frame.Replace("\"stop\"", "null") + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + done,
            "malformed-final-usage" => frame + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":\"invalid\",\"completion_tokens\":100}}\n\n" + done,
            "negative" => frame.Replace("\"completion_tokens\":100", "\"completion_tokens\":-1") + done,
            "zero-input" => frame.Replace("\"prompt_tokens\":1000", "\"prompt_tokens\":0") + done,
            "output-over" => frame.Replace("\"completion_tokens\":100", "\"completion_tokens\":2049") + done,
            "input-over" => frame.Replace("\"prompt_tokens\":1000", "\"prompt_tokens\":1048577") + done,
            "total-mismatch" => frame.Replace("\"completion_tokens\":100", "\"completion_tokens\":100,\"total_tokens\":1") + done,
            "decreasing" => frame + "data: {\"choices\":[]," + usage.Replace(":100}", ":99}") + "}\n\n" + done,
            "after-finish" => frame + "data: {\"choices\":[{\"delta\":{\"content\":\"extra\"}}]," + usage + "}\n\n" + done,
            "uppercase-after-finish" => frame + "DATA: {\"choices\":[{\"delta\":{\"content\":\"extra\"}}]," + usage + "}\n\n" + done,
            "after-done" => frame + done + frame,
            "duplicate-done" => frame + done + done,
            "duplicate-usage" => frame.Replace(usage, usage.Replace("1000", "1048576") + "," + usage) + done,
            "duplicate-completion" => frame.Replace("\"completion_tokens\":100", "\"completion_tokens\":2048,\"completion_tokens\":100") + done,
            "bom-decreasing" => "\uFEFF" + frame.Replace("\"stop\"", "null").Replace("\"completion_tokens\":100", "\"completion_tokens\":200") + frame + done,
            "bom-duplicate" => "\uFEFF" + frame.Replace("\"stop\"", "null").Replace(usage, usage + "," + usage) + frame + done,
            "reasoning-alias-after-finish" => frame + "data: {\"choices\":[{\"delta\":{\"reasoning\":\"extra\"}}]," + usage + "}\n\n" + done,
            _ => "data: {invalid}\n\n" + done,
        };
        host.Respond = (_, _) => Task.FromResult(Response(body));
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_usage_only_frame_is_accepted_and_reasoning_is_not_charged_twice(bool bom)
    {
        using var host = new BatchFixture();
        host.Respond = (_, _) => Task.FromResult(Response((bom ? "\uFEFF" : "") + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"fixture\"},\"finish_reason\":\"length\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":2048,\"completion_tokens_details\":{\"reasoning_tokens\":2048},\"total_tokens\":3048}}\n\ndata: [DONE]\n\n"));
        await host.Send();
        Assert.Equal(18_384, host.Batch.Snapshot.CommittedMicros);
    }

    [Theory]
    [InlineData(512 * 1024 + 1)]
    [InlineData(8 * 1024 * 1024 + 1)]
    public async Task Response_memory_and_line_bounds_fail_closed(int characters)
    {
        using var host = new BatchFixture();
        host.Respond = (_, _) => Task.FromResult(Response(new string('x', characters)));
        Assert.Equal("P2_RESPONSE_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_USAGE_UNKNOWN", host.Batch.Snapshot.StopCode);
    }

    [Fact]
    public void Invalid_policy_and_stale_approval_cannot_expand_the_limit()
    {
        using var host = new BatchFixture();
        host.Batch.Dispose();
        foreach (var policy in new[] { host.Policy with { MaxRequests = 21 }, host.Policy with { CostCapMicros = 3_000_001 },
            host.Policy with { ExpiresUtc = P2BatchPolicy.PriceValidUntil.AddSeconds(1) }, host.Policy with { FixtureFingerprint = "invalid" } })
            Assert.Throws<P2BudgetException>(() => P2BatchProvider.Open(host.LedgerPath, policy, host.Approval,
                host.Credentials, () => throw new InvalidOperationException(), host.Clock));
    }

    [Fact]
    public async Task Model_discovery_is_offline_and_primary_and_auxiliary_requests_share_one_ledger()
    {
        using var host = new BatchFixture();
        Assert.Equal(P2BatchPolicy.Model, Assert.Single(await host.Batch.ListModelsAsync(default)).Id);
        Assert.Equal(0, host.Credentials.Reads);
        Assert.Equal(0, host.TransportCreations);
        var purposes = new[] { "primary", "history_recall", "context_summary", "memory_extraction" };
        foreach (var purpose in purposes)
        {
            host.Respond = async (request, token) =>
            {
                Assert.Equal(P2BatchPolicy.Endpoint, request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal(P2BatchPolicy.Model, body.RootElement.GetProperty("model").GetString());
                Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
                Assert.Equal(purpose != "primary", body.RootElement.TryGetProperty("thinking", out _));
                return CompleteResponse();
            };
            await host.Send(new("fixture", "conversation", [new(ChatMessageRole.User, "合成测试")],
                metadata: purpose == "primary" ? null : new Dictionary<string, string> { ["fgo_auxiliary"] = purpose }, maxOutputTokens: 2048));
        }
        Assert.Equal(4, host.Batch.Snapshot.Requests);
        Assert.Equal(11_200, host.Batch.Snapshot.CommittedMicros);
    }

    [Fact]
    public void Missing_corrupt_or_mismatched_ledger_never_creates_a_fresh_budget()
    {
        using var host = new BatchFixture();
        Assert.Throws<IOException>(() => P2BatchProvider.CreateUnapproved(host.LedgerPath, host.Policy,
            host.Credentials, () => throw new InvalidOperationException(), host.Clock));
        host.Batch.Dispose();
        var original = File.ReadAllText(host.LedgerPath);
        Assert.Throws<P2BudgetException>(() => P2BatchProvider.Open(host.LedgerPath, host.Policy with { CostCapMicros = 2_900_000 },
            host.Approval, host.Credentials, () => throw new InvalidOperationException(), host.Clock));
        File.WriteAllText(host.LedgerPath, original + "{\"Sequence\":1");
        Assert.Throws<P2BudgetException>(() => host.Reopen());
        File.Delete(host.LedgerPath);
        Assert.Throws<FileNotFoundException>(() => host.Reopen());
        Assert.False(File.Exists(host.LedgerPath));
        Assert.Equal(0, host.Sends);
    }

    [Fact]
    public async Task Crash_after_debit_retains_reservation_on_reopen()
    {
        using var host = new BatchFixture();
        host.OnSend = () => host.Batch.Dispose(); // Simulate loss after durable reserve, without an unknown event.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Send());
        host.Reopen();
        Assert.Equal(1, host.Batch.Snapshot.Requests);
        Assert.Equal(2_113_536, host.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => host.Send())).Code);
        Assert.Equal(1, host.Sends);
    }

    [Fact]
    public async Task Identical_policy_cannot_reuse_approval_after_ledger_recreation()
    {
        using var host = new BatchFixture();
        await host.Send();
        var oldApproval = host.Approval;
        host.Batch.Dispose();
        File.Delete(host.LedgerPath);
        using (P2BatchProvider.CreateUnapproved(host.LedgerPath, host.Policy, host.Credentials,
            () => new FixtureHandler((_, _) => Task.FromResult(CompleteResponse())), host.Clock)) { }
        using var reopened = P2BatchProvider.Open(host.LedgerPath, host.Policy, oldApproval, host.Credentials,
            () => new FixtureHandler((_, _) => Task.FromResult(CompleteResponse())), host.Clock);
        Assert.Equal("P2_APPROVAL_REQUIRED", (await Assert.ThrowsAsync<P2BudgetException>(() => Consume(reopened, Request()))).Code);
    }

    [Fact]
    public void Approved_policy_cannot_be_used_to_create_or_open_an_alternate_ledger()
    {
        using var host = new BatchFixture();
        var alternate = Path.Combine(host.DirectoryPath, "alternate.jsonl");
        Assert.Equal("P2_LEDGER_PATH", Assert.Throws<P2BudgetException>(() =>
            P2BatchProvider.CreateUnapproved(alternate, host.Policy, host.Credentials,
                () => throw new InvalidOperationException(), host.Clock)).Code);
        Assert.Equal("P2_LEDGER_PATH", Assert.Throws<P2BudgetException>(() =>
            P2BatchProvider.Open(alternate, host.Policy, host.Approval, host.Credentials,
                () => throw new InvalidOperationException(), host.Clock)).Code);
        Assert.False(File.Exists(alternate));
    }

    [Fact]
    public async Task Journal_contains_only_safe_accounting_fields_and_direct_transport_disables_redirects()
    {
        using var host = new BatchFixture();
        await host.Send();
        host.Batch.Dispose();
        var journal = File.ReadAllText(host.LedgerPath);
        Assert.DoesNotContain("synthetic-key", journal);
        Assert.DoesNotContain("合成测试", journal);
        Assert.DoesNotContain("fixture", journal);
        var entries = journal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(new[] { "created", "reserved", "settled" }, entries.Select(e => e.RootElement.GetProperty("Kind").GetString()));
            Assert.All(entries, e => Assert.Equal(host.Clock.GetUtcNow().ToUnixTimeMilliseconds(), e.RootElement.GetProperty("At").GetInt64()));
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
        using var transport = Assert.IsType<SocketsHttpHandler>(P2BatchProvider.CreateDirectTransport());
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseProxy);
    }

    internal sealed class BatchFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "fgo-p2-budget-" + Guid.NewGuid().ToString("N"));
        public string LedgerPath => Path.Combine(DirectoryPath, "batch.jsonl");
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        public P2BatchPolicy Policy { get; }
        public string Approval { get; }
        public P2BatchProvider Batch { get; private set; }
        public FixtureCredentials Credentials { get; } = new();
        public int TransportCreations { get; private set; }
        public int Sends { get; private set; }
        public Action? OnSend { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
        public List<string> Wires { get; } = [];
        private HttpResponseMessage? pendingNativeFinal;
        public BatchFixture(bool approved = true, long capMicros = 3_000_000, bool subscription = false, int? maxRequests = 20)
        {
            Directory.CreateDirectory(DirectoryPath);
            Policy = new(Guid.NewGuid().ToString("N"), new string('a', 64), maxRequests, subscription ? 0 : capMicros,
                Clock.GetUtcNow().AddHours(1), LedgerPath) { LocalSubscription = subscription };
            using (var prepared = P2BatchProvider.CreateUnapproved(LedgerPath, Policy, Credentials, CreateTransport, Clock))
                Approval = prepared.ApprovalFingerprint;
            Batch = P2BatchProvider.Open(LedgerPath, Policy, approved ? Approval : null, Credentials, CreateTransport, Clock);
        }
        private HttpMessageHandler CreateTransport()
        {
            TransportCreations++;
            return new FixtureHandler(async (request, token) =>
            {
                Sends++;
                OnSend?.Invoke();
                var payload = await request.Content!.ReadAsStringAsync(token);
                Wires.Add(payload);
                if (pendingNativeFinal is { } pending)
                {
                    pendingNativeFinal = null;
                    using var continuation = JsonDocument.Parse(payload);
                    var resumed = !continuation.RootElement.TryGetProperty("tools", out var continuationTools) || continuationTools.GetArrayLength() == 0;
                    if (resumed) return pending;
                    pending.Dispose();
                }
                var response = Respond is null ? CompleteResponse() : await Respond(request, token);
                // Synthetic server only: emit a counted, body-free phase signal before a queued
                // answer. Never synthesize over length, unknown usage, malformed SSE or HTTP errors.
                using var input = JsonDocument.Parse(payload);
                var toolStep = input.RootElement.TryGetProperty("messages", out var messages)
                    && messages.EnumerateArray().Any(message => message.TryGetProperty("content", out var content)
                        && content.GetString() == FgoPet.Kernel.Agent.NativeFinalization.Instructions);
                if (!toolStep || !response.IsSuccessStatusCode) return response;
                var body = await response.Content.ReadAsStringAsync(token);
                var summary = FgoPet.Testing.Persona.P2ResponseDiagnostics.ReadWire(System.Text.Encoding.UTF8.GetBytes(body));
                var knownUsage = false;
                foreach (var line in body.Split('\n'))
                {
                    if (!line.StartsWith("data: {", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var frame = JsonDocument.Parse(line[6..]);
                        knownUsage |= frame.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                            && usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var promptCount) && promptCount >= 0
                            && usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt64(out var completionCount) && completionCount > 0;
                    }
                    catch (JsonException) { }
                }
                if (summary is not { Finish: "stop", HasToolCalls: false, Body.Complete: true } || !knownUsage) return response;
                pendingNativeFinal = response;
                var id = "finish-" + Guid.NewGuid().ToString("N");
                return Response("data: " + JsonSerializer.Serialize(new { id = "chatcmpl-" + id,
                    choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id, type = "function",
                        function = new { name = FgoPet.Kernel.Agent.ModelToolNameMap.GetWireName("response.finalize"), arguments = "{}" } } } }, finish_reason = "tool_calls" } },
                    usage = new { prompt_tokens = 1000, completion_tokens = 100 } }) + "\n\ndata: [DONE]\n\n");
            });
        }
        public Task Send(ChatRequest? request = null, CancellationToken token = default) => Consume(Batch, request ?? Request(), token);
        public void Reopen(bool approved = true)
        {
            Batch.Dispose();
            Batch = P2BatchProvider.Open(LedgerPath, Policy, approved ? Approval : null,
                Credentials, CreateTransport, Clock);
        }
        public void Dispose()
        {
            pendingNativeFinal?.Dispose();
            Batch.Dispose();
            var evidence = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence) && File.Exists(LedgerPath))
            {
                var directory = Path.Combine(evidence, "synthetic-ledgers");
                Directory.CreateDirectory(directory);
                File.Copy(LedgerPath, Path.Combine(directory, Policy.Fingerprint + ".jsonl"));
            }
            File.Delete(LedgerPath);
            Directory.Delete(DirectoryPath);
        }
    }

    internal sealed class FixtureCredentials : ICredentialReader
    {
        public int Reads { get; private set; }
        public string? LastTarget { get; private set; }
        public Action? OnRead { get; set; }
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            Reads++;
            LastTarget = target;
            OnRead?.Invoke();
            return Task.FromResult<string?>("synthetic-key");
        }
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class ObservedClock(Func<DateTimeOffset> now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now(); }
    private static ChatRequest Request(int? output = 2048) => new("fixture", "conversation", [new(ChatMessageRole.User, "合成测试")], maxOutputTokens: output);
    private static async Task Consume(IChatProvider provider, ChatRequest request, CancellationToken token = default)
    {
        await foreach (var _ in provider.StreamAsync(request, token)) { }
    }
    internal static HttpResponseMessage CompleteResponse() => Response("data: {\"choices\":[{\"delta\":{\"content\":\"fixture\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100}}\n\ndata: [DONE]\n\n");
    internal static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}
