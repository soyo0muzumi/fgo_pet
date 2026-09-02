using System.IO;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay;
using FgoPet.AgentRelay.Storage;
using FgoPet.AgentRuntime;
using FgoPet.AgentRuntime.Pipes;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

/// <summary>Production clients and listeners over isolated Windows pipes; no Codex task execution is simulated here.</summary>
public sealed class RelayPairingRoundTripTests
{
    [Fact]
    public async Task Durable_pairing_permissions_event_round_trip_restart_and_revoke_use_the_real_wire_contract()
    {
        var root = Path.Combine(Path.GetTempPath(), "FgoPet-Wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var pipeKey = "fgo-wire-" + Guid.NewGuid().ToString("N");
        var identity = new AdapterIdentityStore(root);
        string sourceId;
        DispatchTaskRequest dispatch = null!;
        AuthenticateRequest revokedAuthentication;
        try
        {
            await using (var relay = new RunningRelay(root, pipeKey))
            {
                await relay.EnableAppAsync(token);
                var connector = relay.CreateConnector(identity);
                sourceId = connector.SourceInstanceId;
                var waiting = await connector.EnsureAuthenticatedAsync(token);
                Assert.Equal(AdapterConnectionStatus.ApprovalRequired, waiting.Status);
                var pending = await relay.ControlAsync("pending_sources", new { }, token);
                var pendingSource = Assert.Single(pending.Payload.GetProperty("sources").EnumerateArray());
                Assert.Equal(sourceId, pendingSource.GetProperty("source_instance_id").GetString());
                Assert.DoesNotContain("credential", pending.ToJson(), StringComparison.OrdinalIgnoreCase);
                await relay.ControlAsync("decide_registration", new RegistrationDecisionRequest(waiting.RequestId!, "approve"), token);

                Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync(token)).Status);
                var approved = await relay.ControlAsync("list_sources", new { }, token);
                var source = Assert.Single(approved.Payload.GetProperty("sources").EnumerateArray());
                Assert.False(source.GetProperty("enabled").GetBoolean());
                Assert.Empty(source.GetProperty("allowed_target_ids").EnumerateArray());
                Assert.DoesNotContain("credential", approved.ToJson(), StringComparison.OrdinalIgnoreCase);

                await relay.ControlAsync("update_permissions", new UpdatePermissionsRequest("codex", sourceId, ["project-a"], true), token);
                dispatch = new DispatchTaskRequest("dispatch-1", "todo-1", "Wire check", null, "normal", null, "project-a")
                { SourceType = "codex", SourceInstanceId = sourceId };
                var accepted = await relay.ControlAsync("dispatch_task", dispatch, token);
                Assert.Equal("accepted", accepted.Payload.GetProperty("result").GetString());
                Assert.Equal(dispatch, Assert.Single(await connector.PollDispatchesAsync(token)));
                // The production adapter poll is peek-only until its journal is durable.
                Assert.Equal(dispatch, Assert.Single(await connector.PollDispatchesAsync(token)));

                await connector.SendEventAsync(new AgentEventMessage("codex", sourceId, "dispatch-1", 1, "task_started",
                    DateTimeOffset.UtcNow, Summary: "Started", DispatchRequestId: "dispatch-1"), token);
                var events = await relay.ControlAsync("status_check", new { include_events = true }, token);
                var eventJson = Assert.Single(events.Payload.GetProperty("events").EnumerateArray());
                var received = ProtocolEnvelope.Parse(eventJson.ValueKind == JsonValueKind.String ? eventJson.GetString()! : eventJson.GetRawText());
                AgentProtocolValidator.Validate(received);
                Assert.Equal("task_started", received.DeserializePayload<AgentEventMessage>().EventType);
                var replayedEvents = await relay.ControlAsync("status_check", new { include_events = true }, token);
                Assert.Single(replayedEvents.Payload.GetProperty("events").EnumerateArray());

                await relay.ControlAsync("update_permissions", new UpdatePermissionsRequest("codex", sourceId, [], true), token);
                var denied = await relay.ControlAsync("dispatch_task", dispatch with { DispatchRequestId = "dispatch-denied" }, token);
                Assert.NotEqual("accepted", denied.Payload.GetProperty("result").GetString());
                Assert.Equal(dispatch, Assert.Single(await connector.PollDispatchesAsync(token)));
                await relay.ControlAsync("update_permissions", new UpdatePermissionsRequest("codex", sourceId, ["project-a"], true), token);
            }

            await using (var restarted = new RunningRelay(root, pipeKey))
            {
                await restarted.EnableAppAsync(token);
                var connector = restarted.CreateConnector(new AdapterIdentityStore(root));
                Assert.Equal(sourceId, connector.SourceInstanceId);
                Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync(token)).Status);
                var sources = await restarted.ControlAsync("list_sources", new { }, token);
                var source = Assert.Single(sources.Payload.GetProperty("sources").EnumerateArray());
                Assert.True(source.GetProperty("enabled").GetBoolean());
                Assert.Equal("project-a", Assert.Single(source.GetProperty("allowed_target_ids").EnumerateArray()).GetString());
                var connection = (await restarted.ControlAsync("connection_test", new { }, token)).DeserializePayload<RelayConnectionTestResponse>();
                Assert.True(connection.RelayOnline);
                Assert.True(connection.AppOnline);
                Assert.True(connection.AdapterOnline);

                // Both unacknowledged deliveries must be replayed after the relay restart.
                Assert.Equal(dispatch, Assert.Single(await connector.PollDispatchesAsync(token)));
                var replayed = await restarted.ControlAsync("status_check", new { include_events = true }, token);
                var replayedEventJson = Assert.Single(replayed.Payload.GetProperty("events").EnumerateArray());
                var replayedEvent = ProtocolEnvelope.Parse(replayedEventJson.ValueKind == JsonValueKind.String
                    ? replayedEventJson.GetString()! : replayedEventJson.GetRawText());
                var replayedMessage = replayedEvent.DeserializePayload<AgentEventMessage>();
                Assert.Equal("task_started", replayedMessage.EventType);

                Assert.Equal("acknowledged", await connector.AcknowledgeDispatchesAsync([dispatch.DispatchRequestId], token));
                Assert.Equal("already_acknowledged", await connector.AcknowledgeDispatchesAsync([dispatch.DispatchRequestId], token));
                var eventAck = await restarted.ControlAsync("event_ack", new EventAcknowledgementRequest(
                    "codex", sourceId, [new EventAcknowledgement(replayedMessage.TaskId, replayedMessage.Sequence)]), token);
                Assert.Equal("acknowledged", eventAck.Payload.GetProperty("result").GetString());
                var eventAckReplay = await restarted.ControlAsync("event_ack", new EventAcknowledgementRequest(
                    "codex", sourceId, [new EventAcknowledgement(replayedMessage.TaskId, replayedMessage.Sequence)]), token);
                Assert.Equal("already_acknowledged", eventAckReplay.Payload.GetProperty("result").GetString());
                Assert.Empty(await connector.PollDispatchesAsync(token));
                var noEventsAfterAck = await restarted.ControlAsync("status_check", new { include_events = true }, token);
                Assert.Empty(noEventsAfterAck.Payload.GetProperty("events").EnumerateArray());
            }

            await using (var restarted = new RunningRelay(root, pipeKey))
            {
                await restarted.EnableAppAsync(token);
                var connector = restarted.CreateConnector(new AdapterIdentityStore(root));
                Assert.Equal(sourceId, connector.SourceInstanceId);
                Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync(token)).Status);
                // Explicit ACKs were durable before this restart, so neither side replays.
                Assert.Empty(await connector.PollDispatchesAsync(token));
                var noEvents = await restarted.ControlAsync("status_check", new { include_events = true }, token);
                Assert.Empty(noEvents.Payload.GetProperty("events").EnumerateArray());

                revokedAuthentication = new("codex", sourceId, identity.LoadOrCreate().Credential!);
                await restarted.ControlAsync("revoke_source", new RevokeSourceRequest("codex", sourceId), token);
                Assert.Equal(AdapterConnectionStatus.Revoked, (await connector.EnsureAuthenticatedAsync(token)).Status);
                Assert.Null(identity.LoadOrCreate().Credential);
            }

            await using (var restarted = new RunningRelay(root, pipeKey))
            {
                await restarted.EnableAppAsync(token);
                var session = new CodexRelaySession(restarted.AdapterPipe, TimeSpan.FromSeconds(2));
                var oldAuthentication = await session.SendAsync(ProtocolEnvelope.Create("old-auth", "authenticate", revokedAuthentication), cancellationToken: token);
                Assert.Equal("revoked", oldAuthentication.Payload.GetProperty("result").GetString());
                Assert.Empty((await restarted.ControlAsync("list_sources", new { }, token)).Payload.GetProperty("sources").EnumerateArray());

                // A new session may request a fresh approval, but never inherits the old grant.
                var connector = restarted.CreateConnector(new AdapterIdentityStore(root));
                var waiting = await connector.EnsureAuthenticatedAsync(token);
                Assert.Equal(AdapterConnectionStatus.ApprovalRequired, waiting.Status);
                await restarted.ControlAsync("decide_registration", new RegistrationDecisionRequest(waiting.RequestId!, "approve"), token);
                Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync(token)).Status);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RunningRelay : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _running;
        private readonly JsonLinePipeClient _control;
        public string AdapterPipe { get; }

        public RunningRelay(string root, string pipeKey)
        {
            AdapterPipe = pipeKey + "-adapter";
            _control = new JsonLinePipeClient(pipeKey + "-app", TimeSpan.FromSeconds(2));
            var host = new RelayHost(new ProtectedRelayStateStore(Path.Combine(root, "AgentRelay")));
            _running = host.RunAsync(AdapterPipe, pipeKey + "-app", _lifetime.Token);
        }

        public CodexRelayConnector CreateConnector(IAdapterIdentityStore store) => new(store,
            new CodexRelaySession(AdapterPipe, TimeSpan.FromSeconds(2)),
            _ => Task.FromResult(new RelayBootstrapResult(RelayBootstrapStatus.Ready, null)));

        public Task<ProtocolEnvelope> EnableAppAsync(CancellationToken token) =>
            ControlAsync("status_check", new { enabled = true, include_events = true }, token);

        public async Task<ProtocolEnvelope> ControlAsync(string type, object payload, CancellationToken token)
        {
            var request = ProtocolEnvelope.Create(Guid.NewGuid().ToString("N"), type, payload);
            var response = ProtocolEnvelope.Parse(await _control.SendAsync(request, token));
            AgentProtocolValidator.ValidateResponse(response);
            Assert.Equal(request.MessageId, response.MessageId);
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { _lifetime.Dispose(); }
        }
    }
}
