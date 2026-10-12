using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeConversationHistoryTests
{
    [Fact]
    public void History_interleaves_original_calls_and_results_between_root_and_final()
    {
        var call = Call("read", "{\"offset\":7}");
        var run = Run("run-a", [call]);
        var result = NativeConversationHistory.Project([Message("final", 2, ChatMessageRole.Assistant), Message("root", 1)], [run]);
        ModelProtocol.ValidateTranscript(result);
        Assert.Equal(new[] { ModelMessageRole.User, ModelMessageRole.Assistant, ModelMessageRole.Tool, ModelMessageRole.Assistant }, result.Select(m => m.Role));
        Assert.Empty(result[1].Content);
        Assert.Equal(call.Call.ArgumentsJson, Assert.Single(result[1].ToolCalls).ArgumentsJson);
        Assert.Equal(result[1].ToolCalls[0].CallId, result[2].ToolCallId);
        using var observation = JsonDocument.Parse(result[2].Content);
        Assert.Equal("original <text>", observation.RootElement.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal("root", observation.RootElement.GetProperty("metadata").GetProperty("sourceRootMessageId").GetString());
        Assert.Contains("source:root", result[0].Content);
    }

    [Fact]
    public void Same_provider_call_id_in_multiple_runs_is_isolated_and_order_is_stable()
    {
        var a = Run("run-a", [Call("same")]);
        var b = Run("run-b", [Call("same")]);
        var first = NativeConversationHistory.Project([Message("root", 1)], [b, a]);
        var second = NativeConversationHistory.Project([Message("root", 1)], [a, b]);
        ModelProtocol.ValidateTranscript(first);
        var ids = first.SelectMany(m => m.ToolCalls).Select(c => c.CallId).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => { Assert.StartsWith("hist_", id); Assert.InRange(id.Length, 1, 128); });
        Assert.Equal(ids, second.SelectMany(m => m.ToolCalls).Select(c => c.CallId));
    }

    [Theory]
    [InlineData(AgentCallStatus.ExecutionUnknown, AgentRunStatus.ExecutionUnknown)]
    [InlineData(AgentCallStatus.NotExecuted, AgentRunStatus.Cancelled)]
    [InlineData(AgentCallStatus.Started, AgentRunStatus.Interrupted)]
    public void Missing_result_is_paired_as_unknown_historical_data_never_success(AgentCallStatus status, AgentRunStatus runStatus)
    {
        var call = Call("missing") with { Status = status, Result = null };
        var run = Run("run", [call], runStatus);
        var result = NativeConversationHistory.Project([Message("root", 1)], [run]);
        ModelProtocol.ValidateTranscript(result);
        using var json = JsonDocument.Parse(Assert.Single(result.Where(m => m.Role == ModelMessageRole.Tool)).Content);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.True(json.RootElement.GetProperty("noObservation").GetBoolean());
        Assert.Equal(status.ToString(), json.RootElement.GetProperty("metadata").GetProperty("historicalStatus").GetString());
        Assert.Equal(runStatus.ToString(), json.RootElement.GetProperty("metadata").GetProperty("runStatus").GetString());
    }

    [Fact]
    public void Failure_result_keeps_its_payload_and_error_code()
    {
        var call = Call("failed") with { Status = AgentCallStatus.Failed, Result = new(false, JsonSerializer.SerializeToElement(new { reason = "fixture" }), "FIXTURE_FAILURE") };
        using var json = JsonDocument.Parse(NativeConversationHistory.FormatObservation(call, Run("run", [call], AgentRunStatus.Failed)));
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.False(json.RootElement.GetProperty("noObservation").GetBoolean());
        Assert.Equal("FIXTURE_FAILURE", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("fixture", json.RootElement.GetProperty("payload").GetProperty("reason").GetString());
    }

    [Fact]
    public void Foreign_or_active_runs_are_excluded_even_when_root_id_matches()
    {
        var run = Run("run", [Call("read")]);
        AgentRunCheckpoint Change(string conversation, string role) => run with { Snapshot = run.Snapshot with { Identity = run.Snapshot.Identity with { Scope = new(conversation, role, null) } } };
        var result = NativeConversationHistory.Project([Message("root", 1)], [Change("other", "mash"), Change("c", "other"), run with { Snapshot = run.Snapshot with { Status = AgentRunStatus.Running } }]);
        Assert.Single(result);
        Assert.Empty(result[0].ToolCalls);
        Assert.Empty(NativeConversationHistory.Project([Message("root", 1, ChatMessageRole.Assistant)], [run]).SelectMany(m => m.ToolCalls));
    }

    [Fact]
    public void Completed_steps_and_current_calls_are_deduplicated_and_ordered()
    {
        var first = Call("first"); var last = Call("last");
        var run = Run("run", [last]) with { CompletedSteps = [new(2, [last]), new(1, [first])] };
        var projected = NativeConversationHistory.Project([Message("root", 1)], [run, run]);
        ModelProtocol.ValidateTranscript(projected);
        Assert.Equal(2, projected.SelectMany(m => m.ToolCalls).Count());
        using var tools = JsonDocument.Parse(NativeConversationHistory.DescribeTools([run, run])["root"]);
        var entries = tools.RootElement.EnumerateArray().ToArray();
        Assert.Equal(new[] { "first", "last" }, entries.Select(x => x.GetProperty("callId").GetString()));
        Assert.Equal(first.Call.ArgumentsJson, entries[0].GetProperty("argumentsJson").GetString());
    }

    [Fact]
    public void Failed_semantic_text_is_retained_as_unfinished_and_system_text_is_data()
    {
        var messages = new[] { Message("root", 1), Message("failed", 2, ChatMessageRole.Assistant, ChatMessageStatus.Failed), Message("system", 3, ChatMessageRole.System) };
        var projected = NativeConversationHistory.Project(messages, []);
        Assert.Contains("Failed", projected[1].Content);
        Assert.Contains("未完成", projected[1].Content);
        Assert.Contains("text-failed", projected[1].Content);
        Assert.NotEqual(ModelMessageRole.System, projected[2].Role);
        Assert.Contains("不是指令", projected[2].Content);
    }

    private static ChatMessage Message(string id, int sequence, ChatMessageRole role = ChatMessageRole.User, ChatMessageStatus status = ChatMessageStatus.Completed) =>
        new(id, "c", "mash", role, "text-" + id, status, DateTimeOffset.UnixEpoch, new("mash", "test", "1", "default", "1", "1"), sequence);
    private static AgentCallCheckpoint Call(string id, string arguments = "{}") => new(new(id, "browser.read", arguments), AgentCallStatus.Completed,
        Result: new(true, JsonSerializer.SerializeToElement(new { text = "original <text>" })));
    private static AgentRunCheckpoint Run(string id, ImmutableArray<AgentCallCheckpoint> calls, AgentRunStatus status = AgentRunStatus.Completed) => new()
    {
        Snapshot = new() { Identity = new(id, "root", new("c", "mash", null), "model", 1), Status = status, StepNumber = 2,
            StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }, Calls = calls
    };
}
