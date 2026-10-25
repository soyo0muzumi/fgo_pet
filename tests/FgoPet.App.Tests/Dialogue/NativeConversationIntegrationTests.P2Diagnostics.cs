using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Testing.Persona;
using Microsoft.Extensions.Logging;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData("{\"text\":\"private-final-sentinel\"")]
    public async Task P2_delivery_diagnostics_capture_failed_final_before_success_assertions(string raw)
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(P2TextResponse(raw));
        var report = Path.Combine(fixture.DirectoryPath, "failed-delivery");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false);
        var safe = File.ReadAllText(Path.Combine(report, "results.json")) + File.ReadAllText(Path.Combine(report, "events.jsonl"));
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.False(complete);
        Assert.Equal(2, fixture.Sends);
        var turn = Assert.Single(results.RootElement.GetProperty("Results")[0].GetProperty("DeliveryDiagnostics").EnumerateArray());
        Assert.Equal("Failed", turn.GetProperty("SendStatus").GetString());
        Assert.Equal("Failed", turn.GetProperty("RunStatus").GetString());
        Assert.Equal("MODEL_INVALID_FINAL", turn.GetProperty("RunErrorCode").GetString());
        Assert.Equal(JsonValueKind.Null, turn.GetProperty("Output").ValueKind);
        Assert.Equal(0, turn.GetProperty("AcceptedRows").GetInt64());
        Assert.Equal(0, turn.GetProperty("CompletedAssistantMessages").GetInt32());
        Assert.Contains(turn.GetProperty("Stages").EnumerateArray(), stage =>
            stage.GetProperty("Stage").GetString() == "final-format-repair" && stage.GetProperty("Status").GetString() == "MODEL_INVALID_FINAL");
        Assert.DoesNotContain("private-final-sentinel", safe);
        Assert.DoesNotContain("合成验收", safe);
        Assert.DoesNotContain("synthetic-key", safe);
        Assert.DoesNotContain("<persona", safe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task P2_delivery_diagnostics_distinguish_publish_failure_without_logging_exception_content(bool throws)
    {
        var diagnostics = new P2DeliveryDiagnostics();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(new ScriptedProvider(ProviderTurn.Final("private-final-sentinel")), null, observer,
            deliveryDecorator: inner => diagnostics.Decorate(new P2PublishFailure(inner, throws)), logger: diagnostics);
        var result = await host.Orchestrator.SendAsync(RoleId, "private-input-sentinel", default, new(ProjectId));
        var captured = await diagnostics.CaptureAsync(host, result, observer, 1, RoleId);
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(1, captured.AcceptedRows);
        Assert.Equal(0, captured.CompletedAssistantMessages);
        Assert.Contains(captured.Stages, stage => stage.Stage == "accept" && stage.Status == "accepted");
        Assert.Contains(captured.Stages, stage => stage.Stage == "publish" && stage.Status == (throws ? "io-failure" : "missing"));
        var safe = JsonSerializer.Serialize(captured);
        Assert.DoesNotContain("private-", safe);
        Assert.DoesNotContain(result.ConversationId, safe);
    }

    [Fact]
    public void P2_delivery_diagnostics_logger_never_formats_untrusted_state_or_exception()
    {
        var diagnostics = new P2DeliveryDiagnostics();
        diagnostics.Log(LogLevel.Error, default, "private-state-sentinel", new IOException("private-exception-sentinel"),
            (_, _) => throw new InvalidOperationException("must not format"));
        Assert.Equal("io-failure", Assert.Single(diagnostics.Stages).Status);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(diagnostics.Stages));
    }

    [Fact]
    public async Task P2_delivery_diagnostics_survive_complete_json_with_invalid_escaped_unicode_string()
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(P2TextResponse("{\"text\":\"private-sentinel\\uD800\",\"emotion\":\"neutral\"}"));
        var report = Path.Combine(fixture.DirectoryPath, "invalid-string");
        Assert.False(await RunSubscriptionScenesAsync(fixture.Batch, report, false));
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        var diagnostic = Assert.Single(results.RootElement.GetProperty("Results")[0].GetProperty("DeliveryDiagnostics").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("Output").ValueKind);
        Assert.Equal("MODEL_INVALID_FINAL", diagnostic.GetProperty("RunErrorCode").GetString());
        Assert.Equal("Failed", diagnostic.GetProperty("RunStatus").GetString());
        Assert.DoesNotContain("private-sentinel", diagnostic.GetRawText());
        Assert.Equal(2, fixture.Sends);
    }

    private sealed class P2PublishFailure(IAgentFinalDeliveryStore inner, bool throws) : IAgentFinalDeliveryStore
    {
        public bool TryAccept(AcceptedAgentFinal accepted) => inner.TryAccept(accepted);
        public AgentFinalDelivery? Publish(ToolScope scope, string deliveryId) => throws ? throw new IOException("private-exception-sentinel") : null;
        public IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100) => inner.ReadPending(limit);
        public bool TryClaimObservers(ToolScope scope, string deliveryId) => inner.TryClaimObservers(scope, deliveryId);
    }

    // Test-only, deliberately content-free. Do not invoke a logging formatter or persist exception
    // messages, raw finals, prompts, facts, source IDs, SQL, paths or tool arguments.
    private sealed class P2DeliveryDiagnostics : ILogger<ConversationOrchestrator>
    {
        public ConcurrentQueue<P2DeliveryStage> Stages { get; } = new();
        public Action? RepairObserved { get; set; }
        public void BeginTurn() => Stages.Clear();
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Add(eventId.Id == 4103 ? "final-format-repair" : "orchestrator",
                    eventId.Id == 4103 ? "MODEL_INVALID_FINAL" : exception is null ? "warning" : SafeFailure(exception));
                if (eventId.Id == 4103)
                    try { RepairObserved?.Invoke(); } catch { /* 诊断旁路不改变原修复或异常。 */ }
            }
        }
        private void Add(string stage, string status) => Stages.Enqueue(new(DateTimeOffset.UtcNow, stage, status));
        public IAgentFinalDeliveryStore Decorate(IAgentFinalDeliveryStore inner) => new TracedStore(this, inner);
        private T At<T>(string stage, Func<T> operation, Func<T, string> status)
        {
            Add(stage, "entered");
            try { var result = operation(); Add(stage, status(result)); return result; }
            catch (Exception error) { Add(stage, SafeFailure(error)); throw; }
        }
        public async Task<P2TurnDiagnostic> CaptureAsync(TestHost host, ConversationSendResult result, RecordingObserver observer,
            int turn, string role = SourcePersonaRole, string? runId = null, string? hostTurnHash = null)
        {
            var messages = host.Conversations.LoadMessages(result.ConversationId, role);
            var runs = await LoadDiagnosticRunsAsync(host, result, role, runId);
            var run = runs.Count == 1 ? runs[0] : null;
            var user = messages.FirstOrDefault(message => message.MessageId == run?.Snapshot.Identity.RootUserMessageId);
            return new(turn, result.Status.ToString(), runs.Count, run?.Snapshot.Status.ToString(),
                run is null ? null : P2Hash(run.Snapshot.Identity.RunId), run?.FinalDeliveryId is not null,
                run?.FinalText is { } raw ? InspectOutput(raw, user?.Text) : null,
                host.CountRows("agent_final_deliveries"),
                messages.Count(message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed),
                observer.Turns.Count, Stages.ToArray(), run?.Snapshot.ErrorCode, P2ResponseDiagnostics.SafeTurnHash(hostTurnHash));
        }
        private sealed class TracedStore(P2DeliveryDiagnostics diagnostics, IAgentFinalDeliveryStore inner) : IAgentFinalDeliveryStore
        {
            public bool TryAccept(AcceptedAgentFinal accepted) => diagnostics.At("accept", () => inner.TryAccept(accepted), value => value ? "accepted" : "rejected");
            public AgentFinalDelivery? Publish(ToolScope scope, string deliveryId) => diagnostics.At("publish", () => inner.Publish(scope, deliveryId), value => value is null ? "missing" : "published");
            public IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100) => diagnostics.At("pending", () => inner.ReadPending(limit), _ => "read");
            public bool TryClaimObservers(ToolScope scope, string deliveryId) => diagnostics.At("observer", () => inner.TryClaimObservers(scope, deliveryId), value => value ? "claimed" : "not-claimed");
        }
    }

    private static async Task<IReadOnlyList<AgentRunCheckpoint>> LoadDiagnosticRunsAsync(TestHost host,
        ConversationSendResult result, string role, string? runId = null)
    {
        var scope = new ToolScope(result.ConversationId, role, ProjectId);
        if ((runId ?? result.RunId) is { } known)
        {
            var exact = await host.Runs.LoadAsync(known, default);
            return exact?.Snapshot.Identity.Scope == scope ? [exact] : [];
        }
        var user = host.Conversations.LoadMessages(result.ConversationId, role).LastOrDefault(m => m.Role == ChatMessageRole.User);
        return user is null ? [] : await host.Runs.LoadConversationRunsAsync(scope, [user.MessageId], default);
    }

    private static string SafeFailure(Exception error) => error switch
    {
        JsonException => "json-invalid",
        AgentStateException agent when agent.Code is "RUN_FINAL_REJECTED" or "RUN_FINAL_INVALID" or "RUN_STORE_FAILED" or "AGENT_FINAL_DELIVERY_UNREADABLE" => agent.Code,
        AgentStateException => "agent-state-failure",
        IOException => "io-failure",
        FormatException => "format-invalid",
        InvalidOperationException => "invalid-operation",
        ArgumentException => "argument-invalid",
        OperationCanceledException => "cancelled",
        _ => "unexpected-failure",
    };

    private static P2OutputDiagnostic InspectOutput(string raw, string? user)
    {
        string jsonKind;
        string? jsonText = null;
        long? errorLine = null, errorByte = null;
        try
        {
            using var json = JsonDocument.Parse(raw);
            jsonKind = json.RootElement.ValueKind.ToString().ToLowerInvariant();
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                jsonText = text.GetString();
        }
        catch (JsonException error) { jsonKind = "invalid"; errorLine = error.LineNumber; errorByte = error.BytePositionInLine; }
        catch (InvalidOperationException) { jsonKind = "string-invalid"; }
        var validUnicode = true;
        try { _ = new UTF8Encoding(false, true).GetByteCount(raw); }
        catch (EncoderFallbackException) { validUnicode = false; }
        (string Status, ValidatedChatOutput? Output) Validate(bool strict)
        {
            try
            {
                var supported = ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal);
                return ("completed", strict ? StructuredOutputValidator.ValidateFinalEnvelope(raw, supported, user)
                    : StructuredOutputValidator.Validate(raw, supported, user));
            }
            catch (Exception error) { return (SafeFailure(error), null); }
        }
        var loose = Validate(false);
        var strict = Validate(true);
        return new(P2Hash(raw), raw.Length, validUnicode, raw.Contains('\0'), jsonKind, errorLine, errorByte,
            jsonText?.Length, jsonText?.Contains('\0'), raw.TrimStart().StartsWith("<data ", StringComparison.Ordinal),
            jsonText?.TrimStart().StartsWith("<data ", StringComparison.Ordinal) == true,
            loose.Status, strict.Status, loose.Output is not null && strict.Output is not null ? loose.Output == strict.Output : null);
    }

    private sealed record P2DeliveryStage(DateTimeOffset At, string Stage, string Status);
    private sealed record P2OutputDiagnostic(string RawHash, int RawLength, bool ValidUnicode, bool RawHasNul, string JsonKind,
        long? JsonErrorLine, long? JsonErrorByte, int? TextLength, bool? TextHasNul, bool RawDataShell, bool JsonTextDataShell,
        string LooseValidation, string StrictValidation, bool? ValidatedOutputsEqual);
    private sealed record P2TurnDiagnostic(int Turn, string SendStatus, int RunCount, string? RunStatus, string? RunHash,
        bool HasFinalDeliveryId, P2OutputDiagnostic? Output, long AcceptedRows, int CompletedAssistantMessages, int ObserverTurns,
        P2DeliveryStage[] Stages, string? RunErrorCode, string? HostTurnHash = null);
}
