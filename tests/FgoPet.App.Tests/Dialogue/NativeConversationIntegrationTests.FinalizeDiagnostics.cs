using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string FinalizeDiagnosticCanary = "private-finalize-canary-中🍵";

    [Theory]
    [InlineData("object", 1, true, false, "NonemptyObject")]
    [InlineData("array", 1, true, false, "NonObject")]
    [InlineData("null", 1, true, false, "NonObject")]
    [InlineData("invalid", 1, true, false, "InvalidJson")]
    [InlineData("empty", 1, true, false, "InvalidJson")]
    [InlineData("scalar", 1, true, false, "NonObject")]
    [InlineData("body", 1, false, true, "EmptyObject")]
    [InlineData("sibling", 2, true, true, "EmptyObject")]
    [InlineData("mixed-controls", 2, true, false, "EmptyObject,NonemptyObject")]
    public async Task Finalize_diagnostics_record_rejected_shape_without_changing_native_failure(
        string scenario, int count, bool whitespace, bool emptyObject, string argumentState)
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire(scenario));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(fixture.Batch, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic control validation.", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        var run = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal("MODEL_INVALID_FINALIZE", run.Snapshot.ErrorCode);
        Assert.Equal(0, run.Snapshot.ToolCalls);
        Assert.Empty(tool.Invocations);
        Assert.Empty(observer.Turns);
        Assert.Equal(2, fixture.Sends);
        Assert.Equal(2, fixture.Batch.Snapshot.Requests);
        Assert.Null(fixture.Batch.Snapshot.StopCode);

        var terminal = Assert.Single(events.Where(item => item.Stage == "settlement" && item.Attempt == 1));
        Assert.Equal("completed", terminal.Status);
        Assert.Equal(FinalizeWireHash(scenario), terminal.Response!.RequestHash);
        var state = FinalizeRejection(terminal);
        Assert.Equal(new[] { "ArgumentStates", "ArgumentsEmptyObject", "BodyWhitespaceOnly", "CallCount", "ResolvedFinalize" },
            state.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(count, state.GetProperty("CallCount").GetInt32());
        Assert.True(state.GetProperty("ResolvedFinalize").GetBoolean());
        Assert.Equal(whitespace, state.GetProperty("BodyWhitespaceOnly").GetBoolean());
        Assert.Equal(emptyObject, state.GetProperty("ArgumentsEmptyObject").GetBoolean());
        Assert.Equal(argumentState.Split(','), state.GetProperty("ArgumentStates").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, events.Count(HasFinalizeRejection));
        Assert.DoesNotContain(FinalizeDiagnosticCanary, JsonSerializer.Serialize(events));
        Assert.DoesNotContain("private-argument-key", JsonSerializer.Serialize(events));
        WriteFinalizeDiagnosticEvidence(scenario, events);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("business")]
    [InlineData("unoffered")]
    [InlineData("canonical")]
    public async Task Finalize_diagnostics_do_not_label_valid_or_unresolved_calls_as_rejections(string scenario)
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire(scenario));
        await fixture.Send(FinalizeDiagnosticRequest(offerControl: scenario != "unoffered"));
        Assert.DoesNotContain(events, HasFinalizeRejection);
        Assert.Equal(1, fixture.Sends);
        Assert.Null(fixture.Batch.Snapshot.StopCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalize_diagnostics_sink_failure_keeps_native_error_and_single_control_correction(bool throws)
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Batch.Diagnostic = throws ? _ => throw new IOException(FinalizeDiagnosticCanary) : null;
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire("object"));
        await using var host = await TestHost.CreateAsync(fixture.Batch, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver());
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic control validation.", default, new(ProjectId));
        Assert.Equal("MODEL_INVALID_FINALIZE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
        Assert.Equal(2, fixture.Sends);
        Assert.Null(fixture.Batch.Snapshot.StopCode);
    }

    [Fact]
    public async Task Finalize_diagnostics_valid_handoff_delivers_once_without_format_repair()
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        fixture.Respond = (_, _) => Task.FromResult(fixture.Sends == 1 ? FinalizeDiagnosticWire("valid")
            : P2TextResponse("{\"text\":\"synthetic final\",\"emotion\":\"neutral\"}"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(fixture.Batch, new RecordingTool(ToolEffect.ReadOnly), observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic control validation.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Single(observer.Turns);
        Assert.Equal(2, fixture.Sends);
        Assert.DoesNotContain(events, HasFinalizeRejection);
    }

    [Fact]
    public async Task Finalize_diagnostics_attempts_do_not_reuse_previous_rejection()
    {
        using var fixture = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = events.Add;
        foreach (var scenario in new[] { "body", "object", "valid" })
        {
            fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire(scenario));
            await fixture.Send(FinalizeDiagnosticRequest());
        }
        var terminal = events.Where(item => item.Stage == "settlement").ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, terminal.Select(item => item.Attempt));
        Assert.Equal(FinalizeWireHash("body"), terminal[0].Response!.RequestHash);
        Assert.False(FinalizeRejection(terminal[0]).GetProperty("BodyWhitespaceOnly").GetBoolean());
        Assert.True(FinalizeRejection(terminal[0]).GetProperty("ArgumentsEmptyObject").GetBoolean());
        Assert.Equal(FinalizeWireHash("object"), terminal[1].Response!.RequestHash);
        Assert.True(FinalizeRejection(terminal[1]).GetProperty("BodyWhitespaceOnly").GetBoolean());
        Assert.False(FinalizeRejection(terminal[1]).GetProperty("ArgumentsEmptyObject").GetBoolean());
        Assert.False(HasFinalizeRejection(terminal[2]));
        Assert.Equal(3, fixture.Sends);
    }

    [Fact]
    public async Task Finalize_diagnostics_parallel_batches_keep_request_association()
    {
        async Task<P2RequestDiagnostic> Run(string scenario)
        {
            using var fixture = new BatchFixture(subscription: true);
            var events = new List<P2RequestDiagnostic>();
            fixture.Batch.Diagnostic = events.Add;
            fixture.Respond = async (_, _) => { await Task.Yield(); return FinalizeDiagnosticWire(scenario); };
            await fixture.Send(FinalizeDiagnosticRequest());
            return Assert.Single(events.Where(HasFinalizeRejection));
        }
        var terminal = await Task.WhenAll(Run("body"), Run("object"));
        Assert.NotEqual(terminal[0].BatchHash, terminal[1].BatchHash);
        Assert.All(terminal, item => Assert.Equal(1, item.Attempt));
        Assert.Equal(FinalizeWireHash("body"), terminal[0].Response!.RequestHash);
        Assert.False(FinalizeRejection(terminal[0]).GetProperty("BodyWhitespaceOnly").GetBoolean());
        Assert.Equal(FinalizeWireHash("object"), terminal[1].Response!.RequestHash);
        Assert.True(FinalizeRejection(terminal[1]).GetProperty("BodyWhitespaceOnly").GetBoolean());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("early-close")]
    [InlineData("missing-done")]
    public async Task Finalize_diagnostics_incomplete_stream_keeps_unknown_and_has_no_rejection(string scenario)
    {
        using var fixture = new BatchFixture(subscription: true);
        using var cancellation = new CancellationTokenSource();
        var events = new List<P2RequestDiagnostic>();
        fixture.Batch.Diagnostic = item =>
        {
            events.Add(item);
            if (scenario == "cancel" && item.Stage == "http-response") cancellation.Cancel();
        };
        fixture.Respond = (_, _) => Task.FromResult(FinalizeDiagnosticWire("body", done: scenario != "missing-done"));
        if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Send(FinalizeDiagnosticRequest(), cancellation.Token));
        else if (scenario == "missing-done")
            Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => fixture.Send(FinalizeDiagnosticRequest()))).Code);
        else
        {
            await using var stream = fixture.Batch.StreamAsync(FinalizeDiagnosticRequest(), default).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            Assert.False(stream.Current.IsComplete);
        }
        Assert.Equal("P2_USAGE_UNKNOWN", fixture.Batch.Snapshot.StopCode);
        Assert.DoesNotContain(events, HasFinalizeRejection);
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => fixture.Send(FinalizeDiagnosticRequest()))).Code);
        Assert.Equal(1, fixture.Sends);
        Assert.Single(events.Where(item => item.Status == "failed"));
    }

    private static bool HasFinalizeRejection(P2RequestDiagnostic item) => JsonSerializer.SerializeToElement(item)
        .TryGetProperty("FinalizeRejection", out var state) && state.ValueKind == JsonValueKind.Object;

    private static JsonElement FinalizeRejection(P2RequestDiagnostic item)
    {
        var record = JsonSerializer.SerializeToElement(item);
        Assert.True(record.TryGetProperty("FinalizeRejection", out var state) && state.ValueKind == JsonValueKind.Object,
            "A rejected complete control must retain only its safe validation state.");
        return state;
    }

    private static string FinalizeWireHash(string scenario) => P2ResponseDiagnostics.Hash("chatcmpl-finalize-" + scenario);

    private static ChatRequest FinalizeDiagnosticRequest(bool offerControl = true) => ChatRequest.CreateAgent("fixture", "conversation",
        [new(ModelMessageRole.User, "Synthetic control validation.", [])],
        tools: (offerControl ? new[] { FinalizeTool, "fixture.read" } : new[] { "fixture.read" })
            .Select(name => new ChatToolDefinition(ModelToolNameMap.GetWireName(name), "Synthetic tool", "{\"type\":\"object\"}")).ToArray(),
        maxOutputTokens: 2048);

    private static HttpResponseMessage FinalizeDiagnosticWire(string scenario, bool done = true)
    {
        var arguments = scenario switch
        {
            "object" => JsonSerializer.Serialize(new Dictionary<string, string> { ["private-argument-key"] = FinalizeDiagnosticCanary }),
            "array" => "[]", "null" => "null", "invalid" => "{", "empty" => "", "scalar" => "42", _ => " \n{}\t"
        };
        var name = scenario == "business" ? ModelToolNameMap.GetWireName("fixture.read")
            : scenario == "canonical" ? FinalizeTool : ModelToolNameMap.GetWireName(FinalizeTool);
        var wire = new StringBuilder();
        void Frame(object delta, bool terminal = false)
        {
            var frame = new Dictionary<string, object?> { ["id"] = "chatcmpl-finalize-" + scenario,
                ["choices"] = new[] { new { delta, finish_reason = terminal ? "tool_calls" : null } } };
            if (terminal) frame["usage"] = new { prompt_tokens = 1000, completion_tokens = 203 };
            wire.Append("data: ").Append(JsonSerializer.Serialize(frame)).Append("\n\n");
        }
        var split = arguments.Length / 2;
        Frame(new { content = scenario == "body" ? FinalizeDiagnosticCanary : "\r\n\t",
            tool_calls = new[] { new { index = 0, id = "call-" + scenario, type = "function", function = new { name, arguments = arguments[..split] } } } });
        Frame(new { tool_calls = new[] { new { index = 0, function = new { arguments = arguments[split..] } } } });
        if (scenario is "sibling" or "mixed-controls") Frame(new { tool_calls = new[] { new { index = 1, id = "sibling-call", type = "function",
            function = new { name = ModelToolNameMap.GetWireName(scenario == "mixed-controls" ? FinalizeTool : "fixture.read"),
                arguments = scenario == "mixed-controls" ? JsonSerializer.Serialize(new Dictionary<string, string>
                    { ["private-argument-key"] = FinalizeDiagnosticCanary }) : "{}" } } } });
        Frame(new { }, terminal: true);
        if (done) wire.Append("data: [DONE]\n\n");
        return P2BatchProviderTests.Response(wire.ToString());
    }

    private static void WriteFinalizeDiagnosticEvidence(string scenario, List<P2RequestDiagnostic> events)
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "finalize-rejections.jsonl"),
            JsonSerializer.Serialize(new { Scenario = scenario, RealModelCalls = 0, Events = events }) + "\n");
    }
}
