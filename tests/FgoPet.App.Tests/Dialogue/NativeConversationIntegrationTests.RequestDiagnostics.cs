using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_diagnostics_record_twenty_request_refusal_before_credentials_or_http(bool sinkThrows)
    {
        using var fixture = new BatchFixture(subscription: true);
        for (var i = 0; i < 20; i++) await fixture.Send();
        fixture.Batch.Dispose(); // The journal deliberately owns an exclusive handle.
        var before = SHA256.HashData(File.ReadAllBytes(fixture.LedgerPath));
        fixture.Reopen();
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = item =>
        {
            events.Add(item);
            if (sinkThrows) throw new IOException(FinalizeDiagnosticCanary);
        };

        var error = await Assert.ThrowsAsync<P2BudgetException>(() => fixture.Send());

        Assert.Equal("P2_REQUEST_LIMIT", error.Code);
        Assert.Equal(20, fixture.Sends);
        Assert.Equal(20, fixture.Credentials.Reads);
        Assert.Equal(20, fixture.TransportCreations);
        Assert.Equal(20, fixture.Batch.Snapshot.Requests);
        Assert.Null(fixture.Batch.Snapshot.StopCode);
        var item = Assert.Single(events);
        Assert.Equal("pre-dispatch", item.Stage);
        Assert.Equal("rejected", item.Status);
        Assert.Equal(0, item.Attempt); // No reservation or wire request identity exists.
        Assert.Null(item.Response);
        Assert.Null(item.FinalizeRejection);
        Assert.Equal(P2ResponseDiagnostics.Hash(fixture.Policy.BatchId).ToUpperInvariant(), item.BatchHash);
        var safe = JsonSerializer.SerializeToElement(item);
        Assert.Equal(20, safe.GetProperty("PreDispatchRefusal").GetProperty("ReservedRequests").GetInt32());
        Assert.Equal("P2_REQUEST_LIMIT", safe.GetProperty("PreDispatchRefusal").GetProperty("Code").GetString());
        Assert.DoesNotContain(FinalizeDiagnosticCanary, safe.GetRawText());
        fixture.Batch.Dispose();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.LedgerPath)));
        WriteRequestDiagnosticEvidence("twenty-request-refusal", events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_diagnostics_join_native_turn_errors_to_wire_without_order_guessing(bool missingSecondId)
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        string? scope = null;
        var provider = new P2TurnScopedProvider(fixture.Batch, () => scope);
        var diagnostics = new P2DeliveryDiagnostics();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(FgoPet.Extensibility.ToolEffect.ReadOnly),
            observer, deliveryDecorator: diagnostics.Decorate, logger: diagnostics);
        var deliveries = new List<P2TurnDiagnostic>();
        for (var turn = 1; turn <= 2; turn++)
        {
            scope = P2Hash(Guid.NewGuid().ToString("N"));
            var scenario = turn == 1 ? "object" : "array";
            fixture.Respond = async (_, _) =>
            {
                var response = FinalizeDiagnosticWire(scenario);
                if (!missingSecondId || turn != 2) return response;
                var wire = await response.Content.ReadAsStringAsync();
                response.Dispose();
                return P2BatchProviderTests.Response(wire.Replace("\"id\":\"chatcmpl-finalize-array\",", "", StringComparison.Ordinal));
            };
            var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic turn.", default, new(ProjectId));
            var delivery = await diagnostics.CaptureAsync(host, result, observer, turn, RoleId, hostTurnHash: scope);
            deliveries.Add(delivery);
            var terminal = Assert.Single(events.Where(item => item.Status == "completed" && item.HostTurnHash == delivery.HostTurnHash));
            Assert.Equal(scope, terminal.HostTurnHash);
            Assert.Equal(turn, terminal.Attempt);
            Assert.Equal(turn, delivery.Turn);
            Assert.Equal(P2Hash(result.RunId!), delivery.RunHash);
            Assert.Equal("MODEL_INVALID_FINALIZE", delivery.RunErrorCode);
            Assert.False(delivery.HasFinalDeliveryId);
            Assert.Equal(missingSecondId && turn == 2 ? null : FinalizeWireHash(scenario), terminal.Response!.RequestHash);
            Assert.Equal(missingSecondId && turn == 2 ? "missing" : "consistent", terminal.Response.ResponseIdState);
        }
        Assert.NotEqual(deliveries[0].HostTurnHash, deliveries[1].HostTurnHash);
        Assert.NotEqual(deliveries[0].RunHash, deliveries[1].RunHash);
        Assert.Equal(2, fixture.Sends);
        var safe = JsonSerializer.Serialize(new { Provider = events, Deliveries = deliveries });
        Assert.DoesNotContain(FinalizeDiagnosticCanary, safe);
        Assert.DoesNotContain("private-argument-key", safe);
        Assert.All(deliveries, item => Assert.DoesNotContain(item.HostTurnHash!, string.Join("\n", fixture.Wires)));
        WriteRequestDiagnosticEvidence("native-turn-join", events, deliveries);
    }

    [Fact]
    public async Task Request_diagnostics_join_budget_refused_final_to_its_native_turn()
    {
        using var fixture = new BatchFixture(subscription: true, maxRequests: 1);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire("valid"));
        var scope = P2Hash(Guid.NewGuid().ToString("N"));
        var callbacks = 0;
        var provider = new P2MeasuredProvider(new P2TurnScopedProvider(fixture.Batch, () => scope),
            "http://127.0.0.1:8326/v1", _ => callbacks++);
        var diagnostics = new P2DeliveryDiagnostics();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(FgoPet.Extensibility.ToolEffect.ReadOnly),
            observer, deliveryDecorator: diagnostics.Decorate, logger: diagnostics);

        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic bounded final.", default, new(ProjectId));
        var delivery = await diagnostics.CaptureAsync(host, result, observer, 1, RoleId, hostTurnHash: scope);

        Assert.Equal("RUN_OPERATION_FAILED", delivery.RunErrorCode);
        Assert.Equal(P2Hash(result.RunId!), delivery.RunHash);
        Assert.Equal(scope, delivery.HostTurnHash);
        Assert.False(delivery.HasFinalDeliveryId);
        Assert.Equal(2, callbacks);
        Assert.Equal(1, fixture.Sends);
        Assert.Equal(1, fixture.Credentials.Reads);
        Assert.Equal(1, fixture.TransportCreations);
        Assert.Equal(FinalizeWireHash("valid"), Assert.Single(events.Where(item => item.Status == "completed")).Response!.RequestHash);
        var refused = Assert.Single(events.Where(item => item.Status == "rejected"));
        Assert.Equal(scope, refused.HostTurnHash);
        Assert.Equal("P2_REQUEST_LIMIT", refused.PreDispatchRefusal!.Code);
        Assert.Equal(1, refused.PreDispatchRefusal.ReservedRequests);
        Assert.Equal(0, refused.Attempt);
        Assert.Null(refused.Response);
        Assert.Empty(observer.Turns);
        WriteRequestDiagnosticEvidence("native-budget-refused-final", events, [delivery]);
    }

    [Fact]
    public async Task Request_diagnostics_concurrent_refusal_cannot_borrow_active_request_identity_or_close_it()
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new System.Collections.Concurrent.ConcurrentQueue<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Enqueue;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Respond = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return FinalizeDiagnosticWire("object");
        };
        var activeScope = P2Hash(Guid.NewGuid().ToString("N"));
        var refusedScope = P2Hash(Guid.NewGuid().ToString("N"));
        var active = RequestDiagnosticSend(new P2TurnScopedProvider(fixture.Batch, () => activeScope));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 2; i++)
                Assert.Equal("P2_IN_FLIGHT", (await Assert.ThrowsAsync<P2BudgetException>(() =>
                    RequestDiagnosticSend(new P2TurnScopedProvider(fixture.Batch, () => refusedScope)))).Code);
        }
        finally { release.TrySetResult(); await active; }
        Assert.Equal(1, fixture.Sends);
        Assert.Equal(1, fixture.Credentials.Reads);
        Assert.Equal(1, fixture.TransportCreations);
        var terminal = Assert.Single(events.Where(item => item.Status == "completed"));
        Assert.Equal(activeScope, terminal.HostTurnHash);
        Assert.Equal(1, terminal.Attempt);
        Assert.Equal(FinalizeWireHash("object"), terminal.Response!.RequestHash);
        var refused = events.Where(item => item.Status == "rejected").ToArray();
        Assert.Equal(2, refused.Length);
        Assert.All(refused, item =>
        {
            Assert.Equal(refusedScope, item.HostTurnHash);
            Assert.Equal(0, item.Attempt);
            Assert.Equal("P2_IN_FLIGHT", item.PreDispatchRefusal!.Code);
            Assert.Null(item.Response);
            Assert.Null(item.FinalizeRejection);
        });
        WriteRequestDiagnosticEvidence("concurrent-refusal", events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_diagnostics_cancelled_entry_preserves_token_and_has_no_budget_refusal(bool enumeratorToken)
    {
        using var fixture = new BatchFixture(subscription: true, maxRequests: 1);
        await fixture.Send();
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new P2TurnScopedProvider(fixture.Batch, () => P2Hash("synthetic-turn"));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(FinalizeDiagnosticRequest(), enumeratorToken ? default : cancellation.Token)
                .WithCancellation(enumeratorToken ? cancellation.Token : default)) { }
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(events);
        Assert.Equal(1, fixture.Sends);
        Assert.Equal(1, fixture.Credentials.Reads);
        Assert.Equal(1, fixture.TransportCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_diagnostics_unavailable_scope_is_null_without_changing_native_failure(bool getterThrows)
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire("object"));
        var provider = new P2TurnScopedProvider(fixture.Batch, () => getterThrows
            ? throw new IOException(FinalizeDiagnosticCanary) : FinalizeDiagnosticCanary);
        var diagnostics = new P2DeliveryDiagnostics();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(FgoPet.Extensibility.ToolEffect.ReadOnly), observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic turn.", default, new(ProjectId));
        var delivery = await diagnostics.CaptureAsync(host, result, observer, 1, RoleId, hostTurnHash: FinalizeDiagnosticCanary);
        Assert.Equal("MODEL_INVALID_FINALIZE", delivery.RunErrorCode);
        Assert.Null(delivery.HostTurnHash);
        Assert.All(events, item => Assert.Null(item.HostTurnHash));
        Assert.Equal(1, fixture.Sends);
        Assert.DoesNotContain(FinalizeDiagnosticCanary, JsonSerializer.Serialize(new { Provider = events, Delivery = delivery }));
    }

    private static async Task RequestDiagnosticSend(IChatProvider provider)
    {
        await foreach (var _ in provider.StreamAsync(FinalizeDiagnosticRequest(), default)) { }
    }

    private static void WriteRequestDiagnosticEvidence(string scenario, IEnumerable<P2RequestDiagnostic> events,
        IEnumerable<P2TurnDiagnostic>? deliveries = null)
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "request-diagnostics.jsonl"),
            JsonSerializer.Serialize(new { Scenario = scenario, RealModelCalls = 0, Events = events, Deliveries = deliveries }) + "\n");
    }
}
