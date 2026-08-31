using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime.Security;
using FgoPet.CodexAdapter.AppServer;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexDispatchWorkerTests
{
    [Fact]
    public async Task Completed_dispatch_is_not_executed_again_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executor = new Executor();
            for (var run = 0; run < 2; run++)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connector = new Connector(stop, stopWhenPolledAgain: run == 1);
                var worker = new CodexDispatchWorker(connector, executor, root, new Protector());
                try { await worker.RunAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                Assert.True(connector.Polls > 0);
            }
            Assert.Equal(1, executor.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Removing_permission_interrupts_running_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var connector = new Connector(stop);
            var executor = new Executor { WaitForCancellation = true, Started = () => connector.Allowed = false };
            var worker = new CodexDispatchWorker(connector, executor, root, new Protector());
            try { await worker.RunAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Assert.Equal(1, executor.Calls);
            Assert.Contains(connector.Events, e => e.EventType == "task_cancelled");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Executor : ICodexTaskExecutor
    {
        public int Calls;
        public bool WaitForCancellation;
        public Action? Started;
        public async Task<string> ExecuteAsync(DispatchTaskRequest request, Func<string, string?, Task> report, CancellationToken token)
        {
            Calls++;
            await report("task_started", "thread-actual");
            Started?.Invoke();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            return "task_completed";
        }
    }

    private sealed class Connector(CancellationTokenSource stop, bool stopWhenPolledAgain = false) : ICodexRelayConnector
    {
        public string SourceInstanceId => "instance-worker";
        public bool Allowed = true;
        public int Polls;
        public List<AgentEventMessage> Events { get; } = [];
        public Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AdapterConnectionResult(AdapterConnectionStatus.Connected));
        public Task<bool> IsDispatchAllowedAsync(string targetId, CancellationToken cancellationToken = default) => Task.FromResult(Allowed);
        public Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default)
        {
            Polls++;
            if (stopWhenPolledAgain && Polls == 2) stop.Cancel();
            return Task.FromResult<IReadOnlyList<DispatchTaskRequest>>([new("dispatch-1", "todo-1", "Test", null, "normal", null, "project-1")
                { SourceType = "codex", SourceInstanceId = SourceInstanceId }]);
        }
        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
        {
            Events.Add(message);
            if (message.EventType is "task_completed" or "task_cancelled" or "task_failed") stop.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> value) => value.ToArray();
    }
}
