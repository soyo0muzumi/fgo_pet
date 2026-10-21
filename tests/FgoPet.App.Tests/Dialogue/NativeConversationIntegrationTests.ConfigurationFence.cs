using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Connection_revision_change_at_a_read_only_tool_boundary_is_cancelled()
    {
        Action changeConnection = () => throw new InvalidOperationException("FIXTURE_NOT_READY");
        var tool = new ConfigurationFenceTool(() => changeConnection());
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read", "{}"), ProviderTurn.Final("must not publish"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        changeConnection = () =>
        {
            var settings = host.Settings.Load(); var current = settings.ModelConnection!;
            host.Settings.Save(settings with { ModelConnection = new ModelConnectionSettings(current.ProviderId,
                current.BaseUrl, "changed-model", current.ToolsSupported, current.ContextWindowOverride, current.MaxOutputTokens) });
        };
        var result = await host.Orchestrator.SendAsync(RoleId, "synthetic read-only fence", default, new(ProjectId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var codes = await host.SafeRunErrorsAsync(new(result.ConversationId, RoleId, ProjectId));
        Console.WriteLine("NATIVE_CONFIGURATION_FENCE_METADATA_ONLY " + JsonSerializer.Serialize(new {
            phase = "result", result = result.Status.ToString(), codes, toolInvocations = tool.Invocations,
            scriptedModelRequests = provider.Requests.Count, realProviderCalls = 0 }));
        Assert.Equal(1, tool.Invocations);
        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        Assert.Equal("Cancelled:RUN_CANCELLED", codes);
        Assert.Single(provider.Requests);
        Assert.NotNull(result.RunId);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(1, checkpoint!.Snapshot.ModelRequests); // Cancellation does not refund a dispatched request.
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            m => m.Role == ChatMessageRole.Assistant && m.Status == ChatMessageStatus.Completed);
        Assert.Empty(observer.Turns);
        Assert.Equal(0L, host.CountRows("agent_final_deliveries"));
    }

    private sealed class ConfigurationFenceTool(Action change) : IToolProvider
    {
        public int Invocations { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Synthetic read-only fence fixture.",
            """{"type":"object","properties":{},"additionalProperties":false}""", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Invocations++; change();
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { observed = true })));
        }
    }
}
