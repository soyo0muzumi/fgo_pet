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
            List<AgentEventMessage> replayEvents = [];
            for (var run = 0; run < 2; run++)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connector = new Connector(stop, stopWhenPolledAgain: run == 1);
                var worker = new CodexDispatchWorker(connector, executor, root, new Protector());
                try { await worker.RunAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                Assert.True(connector.Polls > 0);
                if (run == 1) replayEvents = connector.Events;
            }
            Assert.Equal(1, executor.Calls);
            Assert.Contains(replayEvents, e => e.EventType == "task_completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Journal_save_failure_does_not_ack_or_execute_dispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource();
            var executor = new Executor();
            var connector = new Connector(stop, cancelAfterPoll: true);
            var worker = new CodexDispatchWorker(connector, executor, root, new FailingProtector());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(stop.Token));

            Assert.Empty(connector.AcknowledgedBatches);
            Assert.Equal(0, executor.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Lost_dispatch_ack_retries_from_journal_without_reexecuting()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var executor = new Executor();
            var connector = new Connector(stop, failFirstAcknowledgement: true);
            var worker = new CodexDispatchWorker(connector, executor, root, new Protector());

            try { await worker.RunAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

            Assert.Equal(1, executor.Calls);
            Assert.True(connector.AcknowledgementAttempts >= 2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Full_journal_rejects_new_dispatch_without_ack_or_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource();
            var executor = new Executor();
            var connector = new Connector(stop, cancelAfterPoll: true, dispatchCount: 513);
            var records = Enumerable.Range(0, 512)
                .Select(index => new CodexDispatchRecord(
                    new DispatchTaskRequest("dispatch-existing-" + index, "todo-" + index, "Test", null, "normal", null, "project-1")
                    { SourceType = "codex", SourceInstanceId = connector.SourceInstanceId },
                    1,
                    "terminal"))
                .ToArray();
            var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(connector.SourceInstanceId)));
            var journal = new FgoPet.AgentRuntime.Storage.AtomicProtectedJsonStore<CodexDispatchRecord[]>(
                Path.Combine(root, "CodexAdapter", "dispatches-" + identityKey + ".v1.json"), new Protector());
            journal.Save(records);
            var worker = new CodexDispatchWorker(connector, executor, root, new Protector());

            await Assert.ThrowsAsync<InvalidDataException>(() => worker.RunAsync(stop.Token));

            Assert.Empty(connector.AcknowledgedBatches);
            Assert.Equal(0, executor.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Full_journal_finishes_already_journaled_batch_before_reporting_capacity()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var executor = new Executor();
            var connector = new Connector(stop, dispatchCount: 2);
            var records = Enumerable.Range(0, 511)
                .Select(index => new CodexDispatchRecord(
                    new DispatchTaskRequest("dispatch-existing-" + index, "todo-" + index, "Test", null, "normal", null, "project-1")
                    { SourceType = "codex", SourceInstanceId = connector.SourceInstanceId },
                    1,
                    "terminal"))
                .ToArray();
            var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(connector.SourceInstanceId)));
            var journal = new FgoPet.AgentRuntime.Storage.AtomicProtectedJsonStore<CodexDispatchRecord[]>(
                Path.Combine(root, "CodexAdapter", "dispatches-" + identityKey + ".v1.json"), new Protector());
            journal.Save(records);
            var worker = new CodexDispatchWorker(connector, executor, root, new Protector());

            await Assert.ThrowsAsync<InvalidDataException>(() => worker.RunAsync(stop.Token));

            Assert.Equal(1, executor.Calls);
            Assert.Contains(connector.AcknowledgedBatches, batch => batch.Contains("dispatch-1"));
            Assert.DoesNotContain(connector.AcknowledgedBatches.SelectMany(batch => batch), id => id == "dispatch-2");
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

    private sealed class Connector(CancellationTokenSource stop, bool stopWhenPolledAgain = false,
        bool cancelAfterPoll = false, int dispatchCount = 1, bool failFirstAcknowledgement = false) : ICodexRelayConnector
    {
        public string SourceInstanceId => "instance-worker";
        public bool Allowed = true;
        public int Polls;
        public List<AgentEventMessage> Events { get; } = [];
        public List<IReadOnlyList<string>> AcknowledgedBatches { get; } = [];
        public int AcknowledgementAttempts;
        public Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AdapterConnectionResult(AdapterConnectionStatus.Connected));
        public Task<bool> IsDispatchAllowedAsync(string targetId, CancellationToken cancellationToken = default) => Task.FromResult(Allowed);
        public Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default)
        {
            Polls++;
            if (stopWhenPolledAgain && Polls == 2) stop.Cancel();
            var requests = Enumerable.Range(0, dispatchCount)
                .Select(index => new DispatchTaskRequest(
                    "dispatch-" + (index + 1),
                    "todo-" + index, "Test", null, "normal", null, "project-1")
                { SourceType = "codex", SourceInstanceId = SourceInstanceId })
                .ToArray();
            if (cancelAfterPoll) stop.Cancel();
            return Task.FromResult<IReadOnlyList<DispatchTaskRequest>>(requests);
        }
        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
        {
            Events.Add(message);
            if (message.EventType is "task_completed" or "task_cancelled" or "task_failed") stop.Cancel();
            return Task.CompletedTask;
        }

        public Task<string> AcknowledgeDispatchesAsync(IReadOnlyList<string> requestIds,
            CancellationToken cancellationToken = default)
        {
            AcknowledgementAttempts++;
            AcknowledgedBatches.Add(requestIds);
            if (failFirstAcknowledgement && AcknowledgementAttempts == 1)
                throw new IOException("ack_lost");
            return Task.FromResult("acknowledged");
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> value) => value.ToArray();
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> value) => throw new IOException("journal_save_failed");
        public byte[] Unprotect(ReadOnlySpan<byte> value) => value.ToArray();
    }
}
