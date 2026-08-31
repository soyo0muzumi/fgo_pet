using System.IO.Pipes;
using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Pipes;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using FgoPet.AgentRuntime.Pipes;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class RelayOutputBoundaryTests
{
    [Fact]
    public async Task App_response_batches_large_events_without_losing_the_remaining_queue()
    {
        var (router, registration, grant) = Create();
        Enqueue(router, grant, "task-1");
        Enqueue(router, grant, "task-2");
        var app = new AppPipeServer(router, "unused", registration);
        for (var index = 0; index < 2; index++)
        {
            var response = await app.ProcessLineAsync(ProtocolEnvelope.Create("poll-" + index, "status_check", new { include_events = true }).ToJson());
            Assert.True(Encoding.UTF8.GetByteCount(response) <= JsonLinePipeClient.MaxFrameBytes);
            Assert.Single(ProtocolEnvelope.Parse(response).Payload.GetProperty("events").EnumerateArray());
            Assert.Equal(1 - index, router.PendingInboundCount);
        }
    }

    [Fact]
    public async Task A_client_that_never_reads_a_response_cannot_hold_the_control_listener_forever()
    {
        var (router, registration, grant) = Create();
        Enqueue(router, grant, "task-1");
        var name = "slow-reader-" + Guid.NewGuid().ToString("N");
        var app = new AppPipeServer(router, name, registration, operationTimeout: TimeSpan.FromMilliseconds(150));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = app.RunAsync(lifetime.Token);
        try
        {
            await using var blocked = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await blocked.ConnectAsync(lifetime.Token);
            var request = ProtocolEnvelope.Create("blocked", "status_check", new { include_events = true });
            await blocked.WriteAsync(Encoding.UTF8.GetBytes(request.ToJson() + "\n"), lifetime.Token);
            var next = new JsonLinePipeClient(name, TimeSpan.FromSeconds(2));
            var response = await next.SendAsync(ProtocolEnvelope.Create("next", "connection_test", new { }), lifetime.Token);
            Assert.True(ProtocolEnvelope.Parse(response).DeserializePayload<RelayConnectionTestResponse>().RelayOnline);
            Assert.Equal(1, router.PendingInboundCount); // A failed write must not consume the batch.
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static (RelayRouter Router, RegistrationService Registration, RegistrationGrant Grant) Create()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var pending = registration.Request(new RegistrationRequestMessage("codex", "Codex", "source-1", "1", "1", new string('a', 64)), DateTimeOffset.UtcNow);
        var grant = registration.Approve(pending.RequestId, DateTimeOffset.UtcNow);
        store.UpdatePermissions(grant.SourceType, grant.SourceInstance, [], true);
        store.SetAcceptEvents(true);
        return (new RelayRouter(store, registration), registration, grant);
    }

    private static void Enqueue(RelayRouter router, RegistrationGrant grant, string taskId) =>
        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(grant, ProtocolEnvelope.Create(taskId, "agent_event",
            new AgentEventMessage(grant.SourceType, grant.SourceInstance, taskId, 1, "task_updated", DateTimeOffset.UtcNow, Summary: new string('a', 600_000))), DateTimeOffset.UtcNow).Result);
}
