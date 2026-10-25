using System.IO;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeRunDiagnosticLogTests
{
    [Fact]
    public void Failure_metadata_is_bounded_and_never_contains_private_identity_or_provider_text()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fgopet-run-diagnostic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "diagnostics.jsonl");
        try
        {
            var log = new NativeRunDiagnosticLog(path);
            var state = Snapshot("private provider error https://fixture.invalid/?api_key=synthetic-secret credential=synthetic-secret System.Exception: private");
            for (var i = 0; i < 300; i++) log.Record(state, "terminal_failure", false);
            var text = File.ReadAllText(path);
            Assert.InRange(new FileInfo(path).Length, 1, NativeRunDiagnosticLog.MaximumBytes);
            Assert.DoesNotContain("private", text);
            Assert.DoesNotContain("synthetic-secret", text);
            Assert.DoesNotContain("https://", text);
            Assert.DoesNotContain("credential=", text);
            Assert.DoesNotContain("System.Exception", text);
            foreach (var line in File.ReadLines(path))
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                Assert.Equal("RUN_FAILED", root.GetProperty("errorCode").GetString());
                Assert.False(root.GetProperty("statePersisted").GetBoolean());
                Assert.Equal(1, root.GetProperty("modelBudgetReservations").GetInt32());
                Assert.Equal("Unknown", root.GetProperty("transportOutcome").GetString());
                Assert.Equal(16, root.GetProperty("runCorrelation").GetString()!.Length);
                Assert.Equal(10, root.EnumerateObject().Count());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Early_runtime_exception_leaves_dispatch_and_persistence_unknown_and_log_io_failure_is_secondary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fgopet-run-diagnostic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "diagnostics.jsonl");
        try
        {
            var log = new NativeRunDiagnosticLog(path);
            log.Record(Snapshot("RUN_STORE_FAILED"), "runtime_exception", null);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("modelBudgetReservations").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("statePersisted").ValueKind);
            new NativeRunDiagnosticLog(directory).Record(Snapshot("RUN_STORE_FAILED"), "terminal_failure", false);
            Assert.True(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static AgentRunSnapshot Snapshot(string code) => new() {
        Identity = new("private-run", "private-message", new ToolScope("private-conversation", "private-role", null), "https://fixture.invalid/?token=synthetic-secret", 0),
        Status = AgentRunStatus.Failed, ErrorCode = code, ModelRequests = 1, StepNumber = 1 };
}
