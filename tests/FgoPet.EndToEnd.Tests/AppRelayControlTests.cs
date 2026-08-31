using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay;
using FgoPet.AgentRuntime;
using FgoPet.CodexAdapter.Relay;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class AppRelayControlTests
{
    [Fact]
    public async Task Desktop_control_client_pairs_dispatches_and_projects_over_real_pipes()
    {
        var key = "fgopet-app-wire-" + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = lifetime.Token;
        var host = new RelayHost(); // In-memory authorization here; DPAPI is covered by the durable wire test.
        var running = host.RunAsync(key + "-adapter", key + "-app", token);
        var control = new AgentControlClient(key + "-app", TimeSpan.FromSeconds(2));
        var admin = new AgentRelayAdministration(control);
        var gateway = new AgentRelayClient(control);
        var session = new CodexRelaySession(key + "-adapter", TimeSpan.FromSeconds(2));
        var projector = new AgentEventProjector();
        var projected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new AgentRelayRuntime(gateway, admin,
            _ => Task.FromResult(new RelayBootstrapResult(RelayBootstrapStatus.Ready, null)), (events, _) =>
            {
                foreach (var agentEvent in events) projector.Apply(agentEvent);
                projected.TrySetResult();
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25));
        try
        {
            var registered = await session.SendAsync(ProtocolEnvelope.Create("register", "registration_request",
                new RegistrationRequestMessage("codex", "Wire Codex", "instance-1", "1", "1", new string('a', 64))), cancellationToken: token);
            var requestId = registered.DeserializePayload<RegistrationStatusResponse>().RequestId;
            Assert.Equal(requestId, Assert.Single((await admin.GetSnapshotAsync(token)).PendingSources).RequestId);
            await admin.DecideRegistrationAsync(requestId, true, token);
            var polled = await session.SendAsync(ProtocolEnvelope.Create("poll", "registration_status",
                new RegistrationStatusRequest(requestId, "instance-1", new string('a', 64))), cancellationToken: token);
            var auth = new AuthenticateRequest("codex", "instance-1", polled.DeserializePayload<RegistrationStatusResponse>().Credential!);
            await session.SendAsync(ProtocolEnvelope.Create("authenticate", "authenticate", auth), cancellationToken: token);
            await admin.UpdatePermissionsAsync("codex", "instance-1", ["project-1"], true, token);
            // Establish the actual App-handler lease before dispatch.
            await gateway.PollPendingEventsAsync(token);
            var accepted = await gateway.DispatchAsync(new AgentDispatchRequest("dispatch-1", "todo-1", "Wire", null,
                TodoPriority.Normal, null, "codex", "project-1") { SourceInstanceId = "instance-1" }, token);
            Assert.Equal(AgentDispatchStatus.Accepted, accepted.Status);
            var permission = await session.SendAsync(ProtocolEnvelope.Create("permission-check", "status_check", new { target_id = "project-1" }), auth, token);
            Assert.True(permission.Payload.GetProperty("dispatch_allowed").GetBoolean());
            var outbound = await session.SendAsync(ProtocolEnvelope.Create("dispatch-poll", "status_check", new { include_dispatches = true }), auth, token);
            Assert.Single(outbound.Payload.GetProperty("dispatches").EnumerateArray());
            await session.SendAsync(ProtocolEnvelope.Create("event-1", "agent_event", new AgentEventMessage("codex", "instance-1",
                "dispatch-1", 1, "task_started", DateTimeOffset.UtcNow)), auth, token);
            await runtime.SetEnabledAsync(true, token);
            await projected.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await runtime.StopAsync(token);
            Assert.Equal(AgentExecutionStatus.Active, Assert.Single(projector.Current).Status);
            var snapshot = await admin.TestConnectionAsync(token);
            Assert.True(snapshot.AppOnline);
            Assert.True(snapshot.AdapterOnline);
            await admin.UpdatePermissionsAsync("codex", "instance-1", [], true, token);
            var removed = await session.SendAsync(ProtocolEnvelope.Create("permission-removed", "status_check", new { target_id = "project-1" }), auth, token);
            Assert.False(removed.Payload.GetProperty("dispatch_allowed").GetBoolean());
            await admin.RevokeSourceAsync("codex", "instance-1", token);
            Assert.Empty((await admin.GetSnapshotAsync(token)).Sources);
        }
        finally
        {
            await runtime.StopAsync();
            await lifetime.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
