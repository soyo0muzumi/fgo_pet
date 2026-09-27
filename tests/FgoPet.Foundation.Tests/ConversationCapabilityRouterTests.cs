using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Conversation;
using Xunit;

namespace FgoPet.Foundation.Tests;

public sealed class ConversationCapabilityRouterTests
{
    private static readonly ToolScope Scope = new("conversation-1", "role-1", "project-1");

    [Fact]
    public async Task Empty_and_inactive_catalogs_cannot_offer_or_invoke_tools()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.calendar");
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        var router = new ConversationCapabilityRouter(catalog, runtime);
        Assert.Empty(router.Tools);
        var result = await router.InvokeAsync("echo", new(Scope, Arguments()), default);
        Assert.False(result.Success);
        Assert.Equal("TOOL_UNAVAILABLE", result.ErrorCode);
        Assert.Empty(new ConversationCapabilityRouter(PluginCatalog.Create([]), new(PluginCatalog.Create([]))).Tools);
    }

    [Fact]
    public async Task Active_tool_uses_the_supplied_scope_and_stops_being_available_after_shutdown()
    {
        var tool = new Tool(ToolEffect.Proposal);
        var plugin = new PluginCatalogTests.SamplePlugin("test.calendar", contributes: false)
            { Contributions = new([tool], [], []) };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var router = new ConversationCapabilityRouter(catalog, runtime);
        Assert.Single(router.Tools);
        Assert.True((await router.InvokeAsync("calendar_draft", new(Scope, Arguments()), default)).Success);
        Assert.Equal(Scope, tool.InvokedScope);
        await runtime.StopAsync();
        Assert.Empty(router.Tools);
    }

    [Fact]
    public async Task Command_tools_are_denied_without_any_provider_invocation()
    {
        var tool = new Tool(ToolEffect.Command);
        var plugin = new PluginCatalogTests.SamplePlugin("test.calendar", contributes: false)
            { Contributions = new([tool], [], []) };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var router = new ConversationCapabilityRouter(catalog, runtime);
        Assert.Empty(router.Tools);
        Assert.Equal("TOOL_REQUIRES_AUTHORIZATION", (await router.InvokeAsync("calendar_draft", new(Scope, Arguments()), default)).ErrorCode);
        Assert.Null(tool.InvokedScope);
    }

    [Fact]
    public async Task Combined_context_including_separators_stays_within_the_prompt_budget()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.contexts", contributes: false)
        { Contributions = PluginContributions.Empty with { Contexts = [new Context(new string('a', 125)), new Context(new string('b', 125))] } };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var context = new ConversationCapabilityRouter(catalog, runtime).BuildContext(Scope, "hello");
        Assert.Equal(ConversationContributionLimits.RuntimeContextChars, context.Length);
        Assert.StartsWith(new string('a', 125) + "\n", context);
        await runtime.StopAsync();
        Assert.Empty(new ConversationCapabilityRouter(catalog, runtime).BuildContext(Scope, "hello"));
    }

    private sealed class Context(string text) : IConversationContextProvider
    {
        public string BuildContext(ToolScope scope, string message) => text;
    }

    [Fact]
    public async Task Post_turn_failure_is_isolated_and_shutdown_fences_every_observer()
    {
        var failing = new Observer(fail: true);
        var succeeding = new Observer(fail: false);
        var plugin = new PluginCatalogTests.SamplePlugin("test.observers", contributes: false)
        { Contributions = PluginContributions.Empty with { PostTurnObservers = [failing, succeeding] } };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        var router = new ConversationCapabilityRouter(catalog, runtime);
        var turn = new ConversationCompletedTurn(Scope, "user", "statement", "assistant", "reply", null, null, default);
        router.ObserveCompletedTurn(turn);
        Assert.Equal(0, failing.Calls);
        await runtime.StartAsync(default);
        router.ObserveCompletedTurn(turn);
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, succeeding.Calls);
        Assert.Equal("POST_TURN_OBSERVER_FAILED", router.LastFailureCode);
        runtime.CloseAdmission();
        router.ObserveCompletedTurn(turn);
        Assert.Equal(1, succeeding.Calls);
    }

    private sealed class Observer(bool fail) : IPostTurnObserver
    {
        public int Calls;
        public void Observe(ConversationCompletedTurn turn)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("private provider failure");
        }
    }

    private static JsonElement Arguments() => JsonSerializer.SerializeToElement(new { title = "sample" });

    private sealed class Tool(ToolEffect effect) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new("calendar_draft", "Test-only calendar draft", "{}", effect);
        public ToolScope? InvokedScope { get; private set; }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            InvokedScope = invocation.Scope;
            return ValueTask.FromResult(new ToolResult(true, invocation.Arguments));
        }
    }
}
