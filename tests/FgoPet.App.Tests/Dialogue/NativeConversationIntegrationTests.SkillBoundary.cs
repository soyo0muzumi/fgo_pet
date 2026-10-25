using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Unresolved_skill_name_does_not_load_or_defer_valid_siblings()
    {
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        // The provider must use the offered wire alias; a raw canonical name is unresolved.
        var provider = new ScriptedProvider(new([
            new ChatStreamChunk(string.Empty, ToolCallDelta: new(0, id: "invalid-load", name: "skill.load", argumentsDelta: "{\"name\":\"daily-report\"}")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new(1, id: "read", name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: "{}")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls")]), ProviderTurn.Final("synthetic final"));
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), skills: true);
        var result = await host.Orchestrator.SendAsync(RoleId, "合成测试：拒绝未解析的技能调用。", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.DoesNotContain(provider.Requests[1].ModelMessages, m => m.Content.StartsWith("Trusted skill ", StringComparison.Ordinal));
        Assert.All(provider.Requests, request => ModelProtocol.ValidateTranscript(request.ModelMessages));
        var checkpoint = (await host.Runs.LoadAsync(result.RunId!, default))!;
        var first = checkpoint.CompletedSteps[0];
        Assert.False(first.Calls[0].Call.IsResolved);
        Assert.Equal(AgentCallStatus.Failed, first.Calls[0].Status);
        Assert.Equal("TOOL_NOT_FOUND", first.Calls[0].Result!.ErrorCode);
        Assert.Equal(AgentCallStatus.Completed, first.Calls[1].Status);
        Assert.Equal("read", Assert.Single(tool.Invocations).ExecutionContext!.CallId);
    }

    [Theory]
    [InlineData(ToolEffect.ReadOnly, false)]
    [InlineData(ToolEffect.ReadOnly, true)]
    [InlineData(ToolEffect.Command, false)]
    [InlineData(ToolEffect.Command, true)]
    public async Task Skill_boundary_persists_deferred_calls_and_requires_fresh_authorized_decision(ToolEffect effect, bool loadLast)
    {
        var tool = new RecordingTool(effect);
        var batch = new[] { (Name: "skill.load", Id: "load", Args: "{\"name\":\"daily-report\"}"),
            (Name: "fixture.read", Id: "old", Args: "{}") };
        if (loadLast) Array.Reverse(batch);
        var chunks = batch.Select((call, index) => new ChatStreamChunk(string.Empty,
            ToolCallDelta: new(index, id: call.Id, name: ModelToolNameMap.GetWireName(call.Name), argumentsDelta: call.Args))).ToList();
        chunks.Add(new(string.Empty, IsComplete: true, FinishReason: "tool_calls"));
        var provider = new ScriptedProvider(new(chunks), ProviderTurn.Call("fixture.read", "fresh", "{}"),
            ProviderTurn.Final("synthetic final"), ProviderTurn.Final("synthetic history answer"));
        var beforeSecond = -1;
        provider.RequestReceived = _ => { if (provider.Requests.Count == 2) beforeSecond = tool.Invocations.Count; };
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), settings: TestSettingsStore.Create(32768), skills: true);
        var result = await host.Orchestrator.SendAsync(RoleId, "合成测试：读取技能后再决定调用。", default, new(ProjectId));
        Assert.True(result.Status == ConversationSendStatus.Completed, $"{result.Status}: {result.SafeError}; requests={provider.Requests.Count}; {await host.SafeRunErrorsAsync(new(result.ConversationId, RoleId, ProjectId))}");
        Assert.Equal(0, beforeSecond);
        Assert.Contains(provider.Requests[1].ModelMessages, m => m.Role == ModelMessageRole.System &&
            m.Content.StartsWith("Trusted skill firstparty.skills:daily-report ", StringComparison.Ordinal));
        Assert.All(provider.Requests, request => ModelProtocol.ValidateTranscript(request.ModelMessages));
        var checkpoint = (await host.Runs.LoadAsync(result.RunId!, default))!;
        var calls = checkpoint.CompletedSteps.SelectMany(s => s.Calls).Concat(checkpoint.Calls).ToArray();
        var deferred = Assert.Single(calls, call => call.Call.CallId == "old");
        Assert.Equal(AgentCallStatus.NotExecuted, deferred.Status);
        Assert.Equal(ToolExecutionState.NotExecuted, deferred.Result!.ExecutionState);
        Assert.Equal("TOOL_DEFERRED_FOR_SKILL_LOAD", deferred.Result.ErrorCode);
        using var deferredData = JsonDocument.Parse(Assert.Single(provider.Requests[1].ModelMessages,
            message => message.Role == ModelMessageRole.Tool && message.ToolCallId == "old").Content);
        var deferredMetadata = deferredData.RootElement.GetProperty("metadata");
        Assert.Equal("current_execution", deferredMetadata.GetProperty("sourceKind").GetString());
        Assert.Equal("NotExecuted", deferredMetadata.GetProperty("callStatus").GetString());
        Assert.Equal(JsonValueKind.Null, deferredMetadata.GetProperty("toolEffect").ValueKind);
        var fresh = Assert.Single(calls, call => call.Call.CallId == "fresh");
        Assert.Equal(effect == ToolEffect.Command ? AgentCallStatus.Denied : AgentCallStatus.Completed, fresh.Status);
        Assert.Equal(effect == ToolEffect.Command ? 0 : 1, tool.Invocations.Count);
        if (effect == ToolEffect.Command) Assert.Equal("TOOL_AUTHORIZATION_DENIED", fresh.Result!.ErrorCode);
        else Assert.Equal("fresh", tool.Invocations[0].ExecutionContext!.CallId);

        var followup = await host.Orchestrator.SendAsync(RoleId, "核对上轮调用状态，不执行新的业务操作。", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, followup.Status);
        Assert.DoesNotContain(provider.Requests[4].ModelMessages, m => m.Role == ModelMessageRole.Tool);
        var history = NativeHistoryEvidence.Read(provider.Requests[4].ModelMessages);
        var restored = Assert.Single(history, m => m.GetProperty("callId").GetString() == "old");
        using var json = JsonDocument.Parse(restored.GetProperty("observation").GetRawText());
        Assert.Equal("NotExecuted", json.RootElement.GetProperty("metadata").GetProperty("historicalStatus").GetString());
        Assert.Equal("NotExecuted", json.RootElement.GetProperty("executionState").GetString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(effect == ToolEffect.Command ? 0 : 1, tool.Invocations.Count);
    }

    [Fact]
    public async Task Skill_boundary_defers_question_and_restart_closes_fresh_wait_without_replay()
    {
        const string question = "{\"questions\":[{\"id\":\"title\",\"question\":\"Synthetic title?\"}]}";
        var provider = new ScriptedProvider(new([
            new ChatStreamChunk(string.Empty, ToolCallDelta: new(0, id: "old-ask", name: ModelToolNameMap.GetWireName("user.ask"), argumentsDelta: question)),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new(1, id: "load", name: ModelToolNameMap.GetWireName("skill.load"), argumentsDelta: "{\"name\":\"daily-report\"}")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls")]),
            ProviderTurn.Call("user.ask", "fresh-ask", question));
        var tool = new RecordingTool(ToolEffect.Command);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), settings: TestSettingsStore.Create(32768), skills: true, questions: true);
        var result = await host.Orchestrator.SendAsync(RoleId, "合成测试：读取技能后再询问标题。", default, new(ProjectId));
        Assert.True(result.Status == ConversationSendStatus.WaitingUserInput, $"{result.Status}: {result.SafeError}; requests={provider.Requests.Count}; {await host.SafeRunErrorsAsync(new(result.ConversationId, RoleId, ProjectId))}");
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].ModelMessages, m => m.Role == ModelMessageRole.System &&
            m.Content.StartsWith("Trusted skill firstparty.skills:daily-report ", StringComparison.Ordinal));
        var before = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal("fresh-ask", before.Waiting!.CallId);
        Assert.Equal(AgentCallStatus.NotExecuted, before.CompletedSteps[0].Calls[0].Status);
        Assert.Equal(1, await host.Runs.CloseInterruptedRunsAsync(default));
        var closed = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal(AgentRunStatus.Interrupted, closed.Snapshot.Status);
        Assert.Equal(AgentCallStatus.NotExecuted, closed.CompletedSteps[0].Calls[0].Status);
        Assert.Equal(ToolExecutionState.NotExecuted, closed.CompletedSteps[0].Calls[0].Result!.ExecutionState);
        Assert.Null(closed.Waiting);
        Assert.Empty(tool.Invocations);
        Assert.Equal(2, provider.Requests.Count);
    }
}
