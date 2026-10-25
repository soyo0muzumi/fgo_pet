using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ContextBudgetDiagnosticTests
{
    [Fact]
    public void Context_log_contains_only_enum_and_count_metadata_never_envelope_content()
    {
        using var temp = new Temp();
        var sink = new NativeRunDiagnosticLog(Path.Combine(temp.Root, "failures.jsonl"));
        var messages = ImmutableArray.Create(new ModelMessage(ModelMessageRole.System,
            "private-prompt-canary https://fixture.invalid/?token=secret-canary", []),
            new ModelMessage(ModelMessageRole.User, "private-message-canary", []));
        var tools = ImmutableArray.Create(new ToolDescriptor("fixture.read", "private-schema-canary",
            """{"type":"object","description":"credential-canary"}""", ToolEffect.ReadOnly));
        ContextBudgetDiagnostics.Observe(sink.RecordContext,
            new(ContextDiagnosticStage.BeforeCompaction) { InputBudget = 5888 },
            new(messages, tools), value => value.Messages.Length * 100 + value.Tools.Length);
        var text = File.ReadAllText(temp.Log);
        foreach (var canary in new[] { "private-prompt", "fixture.invalid", "secret-canary", "private-message",
                     "private-schema", "credential-canary", "fixture.read" })
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("BeforeCompaction", document.RootElement.GetProperty("stage").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("messageCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("toolCount").GetInt32());
        Assert.Equal("IndependentEnvelopesNotAdditive", document.RootElement.GetProperty("measurementBasis").GetString());
    }

    [Fact]
    public void Invalid_enum_metadata_is_discarded_and_invalid_counts_become_unknown()
    {
        using var temp = new Temp();
        var sink = new NativeRunDiagnosticLog(Path.Combine(temp.Root, "failures.jsonl"));
        sink.RecordContext(new((ContextDiagnosticStage)999));
        sink.RecordContext(new(ContextDiagnosticStage.CompactionSkipped) { SummaryValidation = (ContextSummaryValidation)999 });
        Assert.False(File.Exists(temp.Log));
        sink.RecordContext(new(ContextDiagnosticStage.CapacityResolved) {
            CapacitySource = ContextLimitSource.ConservativeFallback, WindowTokens = 8192, InputBudget = -1,
            SummaryValidation = ContextSummaryValidation.OutputLimit, SummaryCharacters = 0, SummaryOutputTokens = 2048 });
        using var json = JsonDocument.Parse(File.ReadAllText(temp.Log));
        Assert.Equal(8192, json.RootElement.GetProperty("windowTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("inputBudget").ValueKind);
        Assert.Equal("OutputLimit", json.RootElement.GetProperty("summaryValidation").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("summaryCharacters").GetInt32());
        Assert.Equal(2048, json.RootElement.GetProperty("summaryOutputTokens").GetInt32());
    }

    [Fact]
    public void Context_log_is_bounded_and_write_failure_does_not_escape()
    {
        using var temp = new Temp();
        var sink = new NativeRunDiagnosticLog(Path.Combine(temp.Root, "failures.jsonl"));
        for (var count = 0; count < 1000; count++)
            sink.RecordContext(new(ContextDiagnosticStage.Rejected) {
                Point = ContextRejectionPoint.FinalStepInputBudget, MeasuredInput = count, InputBudget = 5888 });
        Assert.InRange(new FileInfo(temp.Log).Length, 1, NativeRunDiagnosticLog.MaximumBytes);
        Assert.All(File.ReadLines(temp.Log), line => { using var _ = JsonDocument.Parse(line); });
        var blocker = Path.Combine(temp.Root, "blocker");
        File.WriteAllText(blocker, "synthetic");
        new NativeRunDiagnosticLog(Path.Combine(blocker, "failures.jsonl"))
            .RecordContext(new(ContextDiagnosticStage.Rejected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_budget_rejection_keeps_its_code_even_when_diagnostic_sink_throws(bool sinkThrows)
    {
        var records = new List<ContextBudgetDiagnostic>();
        var catalog = PluginCatalog.Create([]);
        await using var runtime = new PluginRuntime(catalog);
        var assembler = new AgentContextAssembler(catalog, runtime);
        var request = new AgentContextRequest(new("conversation", "role", null), "run", 1,
            new("message", new string('a', 64)), 10, []);
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => assembler.AssembleAsync(request,
            [new(ModelMessageRole.User, "private-rejection-canary", [])], _ => 11, default,
            diagnostic => { records.Add(diagnostic); if (sinkThrows) throw new IOException("private-error-canary"); }).AsTask());
        Assert.Equal("RUN_CONTEXT_TOO_LARGE", error.Code);
        var observed = Assert.Single(records);
        Assert.Equal(ContextDiagnosticStage.Rejected, observed.Stage);
        Assert.Equal(ContextRejectionPoint.InitialInputBudget, observed.Point);
        Assert.Equal(11, observed.MeasuredInput);
        Assert.Equal(10, observed.InputBudget);
    }

    [Fact]
    public void Measuring_and_observing_failures_never_replace_the_original_execution_path()
    {
        ContextBudgetDiagnostics.Observe(_ => throw new IOException("private-diagnostic-error"),
            new(ContextDiagnosticStage.BeforeCompaction),
            new([new(ModelMessageRole.User, "private-body", [])], []),
            _ => throw new InvalidOperationException("private-measurement-error"));
    }

    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fgo-context-diagnostic-" + Guid.NewGuid().ToString("N"));
        public string Log => Path.Combine(Root, "native-context-diagnostics.jsonl");
        public Temp() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("fgo-context-diagnostic-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned test root");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
