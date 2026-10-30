using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Empty_stop_is_distinct_from_invalid_json_and_never_repairs_replays_or_delivers(bool reasoning, bool withRead)
    {
        var chunks = reasoning ? new ChatStreamChunk[] {
            new("", ReasoningDelta: "SYNTHETIC_PRIVATE_REASONING"), new("", true, "stop") }
            : [new("", true, "stop")];
        var turns = new List<ProviderTurn>();
        if (withRead) { turns.Add(ProviderTurn.Call("fixture.read", "read-once", "{}")); turns.Add(ProviderTurn.Call(FinalizeTool, "finish", "{}")); }
        turns.Add(new ProviderTurn(chunks)); turns.Add(ProviderTurn.Final("must not be requested"));
        var provider = new ScriptedProvider(turns.ToArray())
            { RawFinalResponses = true, AutoFinalize = false };
        var tool = withRead ? new RecordingTool(ToolEffect.ReadOnly) : null;
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic final reply request.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Contains("MODEL_NO_VISIBLE_OUTPUT", result.SafeError);
        Assert.Contains("未返回可见答复", result.SafeError);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_REASONING", result.SafeError);
        Assert.Equal(withRead ? 3 : 1, provider.Requests.Count);
        Assert.Equal(withRead ? 1 : 0, tool?.Invocations.Count ?? 0);
        Assert.Empty(observer.Turns);
        Assert.Equal(0, host.CountRows("agent_final_deliveries"));
        var run = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(AgentRunStatus.Failed, run!.Snapshot.Status);
        Assert.Equal("MODEL_NO_VISIBLE_OUTPUT", run.Snapshot.ErrorCode);
        Assert.Equal(withRead ? 3 : 1, run.Snapshot.ModelRequests);
    }
}
