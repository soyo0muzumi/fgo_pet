using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using System.Windows.Threading;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private static ExternalConversationBinding IndependentScope() => new(RoleId, "qq-session-" + Guid.NewGuid().ToString("N"), ProjectId);

    [Fact]
    public Task Independent_running_turn_survives_desktop_history_navigation() => RunInputChatTest(async () =>
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("remote final", release));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        using var desktop = new ConversationViewModel(host.Orchestrator, host.Settings);
        desktop.SetActiveServant(RoleId);
        var authority = new IndependentConversationScope();
        var images = new IndependentConversationImages(authority, TimeProvider.System, () => null);
        using var gateway = new IndependentConversationGateway(desktop, host.Orchestrator, host.Settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System);
        var send = gateway.ExchangeAsync(IndependentScope(), "blocked remote turn");
        try
        {
            await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            desktop.SetActiveConversation(Assert.Single(host.Conversations.ListConversations(RoleId)).ConversationId);
            desktop.NewConversationCommand.Execute(null);
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(ExternalConversationStatus.Completed, (await send).Status);
    });

    [Fact]
    public Task Independent_gateway_persists_its_session_across_owner_restart_and_handles_remote_clarification() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ExternalQuestionTurn(), ProviderTurn.Final("clarified"), ProviderTurn.Final("resumed"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), questions: true);
        using var desktop = new ConversationViewModel(host.Orchestrator, host.Settings);
        desktop.SetActiveServant(RoleId);
        var authority = new IndependentConversationScope();
        var images = new IndependentConversationImages(authority, TimeProvider.System, () => null);
        var scope = IndependentScope();
        using (var gateway = new IndependentConversationGateway(desktop, host.Orchestrator, host.Settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System))
        {
            await gateway.ExchangeAsync(scope, "/新对话");
            Assert.Empty(host.Conversations.ListConversations(RoleId));
            var waiting = await gateway.ExchangeAsync(scope, "ask me");
            Assert.Equal(ExternalConversationStatus.WaitingInput, waiting.Status);
            Assert.Equal("clarified", (await gateway.ExchangeAsync(scope, "2")).Text);
        }
        var id = Assert.Single(host.Conversations.ListConversations(RoleId)).ConversationId;
        using var restarted = new IndependentConversationGateway(desktop, host.Orchestrator, host.Settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System);
        Assert.Equal("resumed", (await restarted.ExchangeAsync(scope, "continue")).Text);
        Assert.Equal(id, Assert.Single(host.Conversations.ListConversations(RoleId)).ConversationId);
        Assert.Empty(desktop.CurrentConversationId);
    });

    [Fact]
    public Task Independent_gateway_never_executes_an_approval_or_leaves_an_invisible_wait() => RunInputChatTest(async () =>
    {
        var tool = new ApprovalFixtureTool("reminder.create", "reminders");
        var provider = new ScriptedProvider(ProviderTurn.Call("reminder.create", "operation", "{\"target\":\"synthetic-id\"}"), ProviderTurn.Final("next answer"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), approvalPlugin: tool);
        using var desktop = new ConversationViewModel(host.Orchestrator, host.Settings);
        desktop.SetActiveServant(RoleId);
        var authority = new IndependentConversationScope();
        var images = new IndependentConversationImages(authority, TimeProvider.System, () => null);
        using var gateway = new IndependentConversationGateway(desktop, host.Orchestrator, host.Settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System);
        var scope = IndependentScope();
        var result = await gateway.ExchangeAsync(scope, "synthetic action");
        Assert.Equal(ExternalConversationStatus.Cancelled, result.Status);
        Assert.Equal(0, tool.Invocations);
        Assert.Contains("桌面", result.Text);
        Assert.Equal("next answer", (await gateway.ExchangeAsync(scope, "ordinary next turn")).Text);
        Assert.Equal(0, tool.Invocations);
    });

    [Fact]
    public Task Independent_gateway_starts_from_blank_desktop_and_continues_after_desktop_switch() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("first"), ProviderTurn.Final("second"), ProviderTurn.Final("new"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        using var desktop = new ConversationViewModel(host.Orchestrator, host.Settings);
        desktop.SetActiveServant(RoleId);
        desktop.InputText = "unsent desktop draft";
        var authority = new IndependentConversationScope();
        var images = new IndependentConversationImages(authority, TimeProvider.System, () => null);
        using var gateway = new IndependentConversationGateway(desktop, host.Orchestrator, host.Settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System);
        var captured = await gateway.CaptureCurrentScopeAsync();
        Assert.NotNull(captured);
        Assert.Empty(desktop.CurrentConversationId);
        var scope = IndependentScope();
        Assert.Equal(ExternalConversationStatus.Completed, (await gateway.ExchangeAsync(scope, "qq first")).Status);
        var id = Assert.Single(host.Conversations.ListConversations(RoleId)).ConversationId;
        desktop.SetActiveServant("other-role");
        Assert.Equal("second", (await gateway.ExchangeAsync(scope, "qq second")).Text);
        Assert.Equal(4, host.Conversations.LoadMessages(id, RoleId).Count);
        var reset = await gateway.ExchangeAsync(scope, "/新对话");
        Assert.Equal(ExternalConversationStatus.Completed, reset.Status);
        Assert.Equal("new", (await gateway.ExchangeAsync(scope, "after reset")).Text);
        Assert.Equal(2, host.Conversations.ListConversations(RoleId).Count);
        Assert.Empty(desktop.CurrentConversationId);
        Assert.Null(host.Conversations.ReadState("LastActiveConversationId:" + RoleId));
        Assert.False(images.Store.HasSelection);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Independent_gateway_image_reaches_only_its_own_model_run_and_preserves_desktop_selection(bool clarification) => RunInputChatTest(async () =>
    {
        var turns = new List<ProviderTurn> { ProviderTurn.Call("image.read", "qq-image", "{}") };
        if (clarification) turns.Add(ExternalQuestionTurn());
        turns.Add(ProviderTurn.Final("image answer"));
        var provider = new ScriptedProvider(turns.ToArray());
        var connection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4.1-mini",
            contextWindowOverride: 32768, maxOutputTokens: 512);
        var route = ModelRouteKey.From(connection);
        var settings = new TestSettingsStore(DialogueSettings.Defaults with { ModelConnection = connection });
        var authority = new IndependentConversationScope();
        var images = new IndependentConversationImages(authority, TimeProvider.System, () => route);
        var desktopImages = new SelectedImageStore(_ => true);
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        desktopImages.Grant(new("desktop", RoleId, ProjectId), route, new(bytes, 1, 1), "desktop-user");
        await using var tool = new ImageToolsPlugin(desktopImages, () => route, selection: s => authority.IsActive(s) ? images.Store : desktopImages);
        await tool.StartAsync(default);
        await using var host = await TestHost.CreateAsync(new ImagePreparingProvider(provider, images.Store, route), tool,
            new RecordingObserver(), settings, context: tool, questions: true);
        using var desktop = new ConversationViewModel(host.Orchestrator, settings);
        desktop.SetActiveServant(RoleId);
        desktop.InputText = "desktop draft";
        using var gateway = new IndependentConversationGateway(desktop, host.Orchestrator, settings, authority, images,
            Dispatcher.CurrentDispatcher, TimeProvider.System);
        var scope = IndependentScope();
        var result = await gateway.ExchangeAsync(scope, "describe this", new(bytes, "image/png"));
        if (clarification)
        {
            Assert.Equal(ExternalConversationStatus.WaitingInput, result.Status);
            Assert.True(images.Store.HasSelection);
            result = await gateway.ExchangeAsync(scope, "2");
        }
        Assert.Equal(ExternalConversationStatus.Completed, result.Status);
        Assert.Contains(provider.Requests, request => request.ModelMessages.Any(m => m.Image is not null));
        Assert.True(desktopImages.HasSelection);
        Assert.False(images.Store.HasSelection);
        Assert.Equal("desktop draft", desktop.InputText);
        Assert.Empty(desktop.CurrentConversationId);
    });

    [Fact]
    public async Task Independent_session_keeps_desktop_history_and_selection_unchanged()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("desktop"), ProviderTurn.Final("qq one"),
            ProviderTurn.Final("qq two"), ProviderTurn.Final("qq new"), ProviderTurn.Final("desktop again"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        var desktop = await host.Orchestrator.SendAsync(RoleId, "desktop request", default, new(ProjectId));
        var remote = host.Orchestrator.CreateIndependentSession("Qq", _ => true);
        var first = await remote.SendAsync(RoleId, "qq first", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, first.Status);
        Assert.NotEqual(desktop.ConversationId, first.ConversationId);
        Assert.Equal(desktop.ConversationId, host.Conversations.ReadState("LastActiveConversationId:" + RoleId));
        host.Orchestrator.StartNewConversation(RoleId);
        var second = await remote.SendAsync(RoleId, "qq second", default, new(ProjectId));
        Assert.Equal(first.ConversationId, second.ConversationId);
        remote.StartNewConversation(RoleId);
        var third = await remote.SendAsync(RoleId, "qq new", default, new(ProjectId));
        Assert.NotEqual(first.ConversationId, third.ConversationId);
        Assert.Equal(4, host.Conversations.LoadMessages(first.ConversationId, RoleId).Count);
        host.Orchestrator.LoadConversation(desktop.ConversationId, RoleId);
        var resumed = await host.Orchestrator.SendAsync(RoleId, "desktop again", default, new(ProjectId));
        Assert.Equal(desktop.ConversationId, resumed.ConversationId);
    }
}
