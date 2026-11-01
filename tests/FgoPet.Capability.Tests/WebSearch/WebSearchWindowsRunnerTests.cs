using System.Collections.Concurrent;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;
using FgoPet.Platform.Windows.Processes;
using FgoPet.Plugin.WebSearch;
using Xunit;

namespace FgoPet.Capability.Tests.WebSearch;

public sealed class WebSearchWindowsRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_entry_points_pass_real_runner_validation_and_report_launch_failure(bool connectionTest)
    {
        // An invalid executable reaches Windows launch without running a helper or accessing the network.
        var worker = Path.Combine(Path.GetTempPath(), "fgopet-search-invalid-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(worker, "synthetic invalid executable");
        try
        {
            var diagnostics = new Diagnostics();
            var grant = new WebSearchSettings(true, "8dc9a81a6aac4720a06fe4b936d69e24", 1);
            var scope = new ToolScope("conversation", "role", "project");
            await using var plugin = new WebSearchPlugin(() => grant, candidate => candidate == scope,
                new WindowsBoundedProcessRunner(diagnostics), worker);
            await plugin.StartAsync(default);

            if (connectionTest)
            {
                var result = await plugin.CheckConnectionAsync(default);
                Assert.False(result.IsAvailable);
                Assert.Equal("WEB_SEARCH_RUNTIME_FAILED", result.ErrorCode);
            }
            else
            {
                var invocation = new ToolInvocation(scope, JsonSerializer.SerializeToElement(new { query = "fixture" }))
                {
                    ExecutionContext = new("run", 1, "call", new string('A', 64))
                };
                var result = await plugin.Contributions.Tools.Single().InvokeAsync(invocation, default);
                Assert.False(result.Success);
                Assert.Equal("WEB_SEARCH_RUNTIME_FAILED", result.ErrorCode);
                Assert.Equal("{}", result.Payload.GetRawText());
            }

            // Process supervision deliberately does not wait indefinitely for its asynchronous sink.
            await diagnostics.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(diagnostics.Events, entry => entry.Stage == BoundedProcessStage.Launching);
            Assert.Contains(diagnostics.Events, entry => entry.Stage == BoundedProcessStage.Failed
                && entry.ErrorCode == "PROCESS_LAUNCH_FAILED");
        }
        finally { File.Delete(worker); }
    }

    private sealed class Diagnostics : IBoundedProcessDiagnostics
    {
        public ConcurrentQueue<BoundedProcessNotification> Events { get; } = new();
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Record(BoundedProcessNotification notification)
        {
            Events.Enqueue(notification);
            if (notification.Stage == BoundedProcessStage.Failed) Failed.TrySetResult();
        }
    }
}
