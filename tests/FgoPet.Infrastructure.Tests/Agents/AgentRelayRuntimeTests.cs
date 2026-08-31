using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class AgentRelayRuntimeTests
{
    [Theory]
    [InlineData("wrong-id", "connection_test", "1", "invalid_response")]
    [InlineData("request", "status_check", "1", "invalid_response")]
    [InlineData("request", "connection_test", "2", "version_mismatch")]
    public async Task Control_rejects_unrelated_or_incompatible_response(string id, string type, string version, string expected)
    {
        var client = new AgentControlClient((_, _) => Task.FromResult(
            (ProtocolEnvelope.Create(id, type, new { }) with { ProtocolVersion = version }).ToJson()));
        var error = await Assert.ThrowsAsync<AgentRelayException>(() => client.SendAsync(
            ProtocolEnvelope.Create("request", "connection_test", new { })));
        Assert.Equal(expected, error.SafeError);
    }

    [Fact]
    public async Task Administration_maps_sources_and_sends_instance_scoped_permissions_without_credentials()
    {
        var requests = new List<ProtocolEnvelope>();
        var client = new AgentControlClient((request, _) =>
        {
            requests.Add(request);
            object payload = request.MessageType switch
            {
                "connection_test" => new RelayConnectionTestResponse(true, true, false, "1", "adapter_offline", DateTimeOffset.UtcNow, null),
                "pending_sources" => new { result = "pending_sources", sources = new[] {
                    new PendingSourceDto("pending-1", "codex", "Codex", "instance-2", "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)) } },
                "list_sources" => new { result = "list_sources", sources = new[] {
                    new ApprovedSourceDto("codex", "Codex", "instance-1", "1", DateTimeOffset.UtcNow, false, new[] { "project-1" }, false) } },
                _ => new { result = "ok" },
            };
            return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType, payload).ToJson());
        });
        var admin = new AgentRelayAdministration(client);
        var snapshot = await admin.GetSnapshotAsync();
        Assert.Equal(AgentRelayConnectionState.AwaitingApproval, snapshot.State);
        Assert.Equal("instance-1", Assert.Single(snapshot.Sources).SourceInstanceId);
        Assert.Equal("instance-2", Assert.Single(snapshot.PendingSources).SourceInstanceId);
        await admin.UpdatePermissionsAsync("codex", "instance-1", ["project-2"], true);
        Assert.Equal("instance-1", requests[^1].Payload.GetProperty("source_instance_id").GetString());
        Assert.DoesNotContain("credential", string.Join("", requests.Select(r => r.ToJson())));
    }

    [Fact]
    public async Task Gateway_dispatch_requires_instance_and_reads_versioned_acknowledgment()
    {
        ProtocolEnvelope? sent = null;
        var gateway = new AgentRelayClient(new AgentControlClient((request, _) =>
        {
            sent = request;
            return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType,
                new { result = "already_applied", task_id = "task-1", source_instance = "instance-1" }).ToJson());
        }));
        var request = new AgentDispatchRequest("dispatch-1", "todo-1", "Work", null, TodoPriority.Normal, null, "codex", "target-1");
        Assert.Equal("source_instance_required", (await gateway.DispatchAsync(request)).SafeError);
        Assert.Null(sent);
        var result = await gateway.DispatchAsync(request with { SourceInstanceId = "instance-1" });
        Assert.Equal(AgentDispatchStatus.AlreadyApplied, result.Status);
        Assert.Equal("instance-1", sent!.Payload.GetProperty("source_instance_id").GetString());
    }

    [Fact]
    public async Task Runtime_starts_once_polls_heartbeat_and_stops_without_disabling_remote_queue()
    {
        var boots = 0;
        var requests = new List<ProtocolEnvelope>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AgentControlClient((request, _) =>
        {
            lock (requests) requests.Add(request);
            object payload = request.MessageType switch
            {
                "connection_test" => new RelayConnectionTestResponse(true, true, false, "1", "adapter_offline", DateTimeOffset.UtcNow, null),
                "pending_sources" or "list_sources" => new { result = request.MessageType, sources = Array.Empty<object>() },
                _ => new { result = "status", events = Array.Empty<object>() },
            };
            return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType, payload).ToJson());
        });
        using var runtime = new AgentRelayRuntime(new AgentRelayClient(client), new AgentRelayAdministration(client),
            _ => { Interlocked.Increment(ref boots); return Task.FromResult(new RelayBootstrapResult(RelayBootstrapStatus.Ready, null)); },
            (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(10));
        runtime.SnapshotChanged += snapshot => { if (snapshot.RelayOnline) observed.TrySetResult(); };
        Assert.Equal(AgentRelayConnectionState.Disabled, runtime.Current.State);
        Assert.Equal(0, boots);
        await runtime.SetEnabledAsync(true);
        await runtime.SetEnabledAsync(true);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.StopAsync();
        Assert.Equal(1, boots);
        Assert.Contains(requests, r => r.Payload.TryGetProperty("include_events", out var value) && value.GetBoolean());
        Assert.DoesNotContain(requests, r => r.Payload.TryGetProperty("enabled", out var value) && !value.GetBoolean());
    }

    [Fact]
    public async Task Disabling_does_not_bootstrap_and_cancels_in_progress_start()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new AgentControlClient((request, _) => Task.FromResult(
            ProtocolEnvelope.Create(request.MessageId, request.MessageType, new { result = "status" }).ToJson()));
        using var runtime = new AgentRelayRuntime(new AgentRelayClient(control), new AgentRelayAdministration(control), async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new RelayBootstrapResult(RelayBootstrapStatus.Ready, null);
        }, (_, _) => Task.CompletedTask);
        await runtime.SetEnabledAsync(false);
        Assert.False(started.Task.IsCompleted);
        await runtime.SetEnabledAsync(true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(AgentRelayConnectionState.Disabled, runtime.Current.State);
    }
}
