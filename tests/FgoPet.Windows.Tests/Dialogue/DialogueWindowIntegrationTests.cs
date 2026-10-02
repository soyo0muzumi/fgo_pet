using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.App.Settings;
using FgoPet.App.Windowing;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Packs;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

// Native owner/bridge coverage. Visual and keyboard DOM behavior is checked against
// the real Chat module by the separate synthetic browser fixture, not WPF controls.
[Trait("Category", "WindowsIntegration")]
public sealed class DialogueWindowIntegrationTests
{
    [Fact]
    public void Host_metadata_refreshes_real_project_choices_and_preserves_saved_unavailable_project() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(projectCatalog: new SampleProjects());
        vm.Conversation.SetActiveServant("800100");
        using var window = new DialogueWindow(vm);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null);
        var changes = 0; host.Changed += (_, _) => changes++;
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.RefreshProjects), default)).Success);
        var project = Assert.Single(host.ReadPresentation().Projects);
        Assert.Equal("project-1", project.Id);
        Assert.True(changes > 0);
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.SelectProject, "project-1"), default)).Success);
        Assert.Equal("project-1", vm.Conversation.SessionContext.ProjectId);
        vm.Conversation.SessionContext.TrySetProject("saved-unavailable", "已保存的项目");
        using var session = new ChatWebSession(vm.Conversation, host);
        Assert.Equal("已保存的项目", session.ReadSnapshot().Conversation.Project.Label);
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.SelectProject, "arbitrary-id"), default)).Success);
        vm.Conversation.IsStreaming = true;
        Assert.False(host.ReadPresentation().Projects[0].CanSelect);
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.SelectProject, "project-1"), default)).Success);
        vm.Conversation.Dispose();
    });

    [Fact]
    public void Clipboard_uses_real_turn_permissions_and_recovers_from_busy_clipboard() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); var clipboard = new ClipboardStub();
        using var window = new DialogueWindow(vm);
        using var host = new ChatWebHostActions(vm, window, clipboard, null, null, null);
        vm.Conversation.Turns.Add(new("reply", ChatMessageRole.Assistant, "纯文本消息"));
        window.Show();
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.CopyTurn, "reply"), default)).Success);
        Assert.Equal("纯文本消息", clipboard.Text);
        clipboard.Throw = true;
        Assert.Equal("CHAT_CLIPBOARD_UNAVAILABLE", Complete(host.HandleAsync(new(ChatWebHostAction.CopyTurn, "reply"), default)).ErrorCode);
        vm.Conversation.Turns.Add(new("reply", ChatMessageRole.Assistant, "重复目标"));
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.CopyTurn, "reply"), default)).Success);
        window.Hide(); clipboard.Throw = false;
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.CopyTurn, "reply"), default)).Success);
        vm.Conversation.Dispose();
    });

    [Fact]
    public void Settings_requests_keep_the_chat_surface_and_unsent_draft() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); vm.Conversation.InputText = "保留草稿";
        using var window = new DialogueWindow(vm);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null);
        var content = ((ContentControl)window.FindName("ChatSurface")).Content;
        var requests = new List<SettingsSection>(); vm.SettingsRequested += requests.Add;
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenPersonalizationSettings), default)).Success);
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenSpeechSettings), default)).Success);
        vm.Conversation.ErrorText = "请配置模型连接";
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenModelSettings), default)).Success);
        Assert.Equal(new[] { SettingsSection.Personalization, SettingsSection.Speech, SettingsSection.ModelConnection }, requests);
        Assert.Same(content, ((ContentControl)window.FindName("ChatSurface")).Content);
        Assert.Equal("保留草稿", vm.Conversation.InputText);
        vm.Conversation.Dispose();
    });

    [Fact]
    public void Workspace_navigation_uses_actual_reply_identity_and_keeps_each_surface_state() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); vm.Conversation.InputText = "草稿";
        var catalog = new SampleWorkspaces();
        var owner = new Window { ShowInTaskbar = false, Width = 100, Height = 100 }; owner.Show();
        using var coordinator = new WorkspaceWindowCoordinator(catalog, () => owner);
        using var window = new DialogueWindow(vm, workspaces: catalog, workspaceNavigation: coordinator.Open);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, catalog, coordinator.Open);
        var turn = new ConversationTurnViewModel("reply", ChatMessageRole.Assistant, "已确认")
            { WorkspaceId = "sample.calendar", CreatedItemId = "event-42" };
        vm.Conversation.Turns.Add(turn);
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspace, "reply"), default)).Success);
        Assert.Equal("event-42", catalog.Views["sample.calendar"].Navigation?.ItemId);
        catalog.Views["sample.calendar"].Text = "未保存的编辑";
        turn.WorkspaceId = "sample.notes";
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspace, "reply"), default)).Success);
        turn.WorkspaceId = "sample.calendar";
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspace, "reply"), default)).Success);
        Assert.Equal("未保存的编辑", catalog.Views["sample.calendar"].Text);
        Assert.Equal(2, catalog.Created);
        Assert.Equal("草稿", vm.Conversation.InputText);
        turn.WorkspaceId = "missing";
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspace, "reply"), default)).Success);
        Assert.Equal(2, catalog.Created);
        Assert.Null(window.FindName("TasksPage"));
        coordinator.Dispose(); owner.Close(); vm.Conversation.Dispose();
    });

    [Fact]
    public void New_workspace_and_legacy_navigation_open_separate_existing_surfaces() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); var catalog = new SampleWorkspaces();
        var routes = new List<(string Workspace, WorkspaceNavigation Navigation)>();
        bool Route(string id, WorkspaceNavigation navigation) { routes.Add((id, navigation)); return true; }
        using var window = new DialogueWindow(vm, workspaces: catalog, workspaceNavigation: Route);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, catalog, Route);
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.NewWorkspaceItem), default)).Success);
        Assert.Equal(WorkspaceNavigationKind.NewItem, routes[0].Navigation.Kind);
        vm.NavigateTo(MainNavigationTarget.Schedule, selectedId: "existing-42"); StaRunner.Pump();
        Assert.Equal(WorkspaceNavigationKind.ExistingItem, routes[^1].Navigation.Kind);
        Assert.Equal("existing-42", routes[^1].Navigation.ItemId);
        Assert.True(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspaceOverview), default)).Success);
        Assert.Equal(WorkspaceNavigationKind.Overview, routes[^1].Navigation.Kind);
        vm.Conversation.Dispose();
    });

    [Fact]
    public void Missing_capabilities_are_unavailable_without_replacing_chat() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); using var window = new DialogueWindow(vm);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null);
        var availability = host.ReadPresentation().Actions!;
        Assert.False(availability.CanOpenFocus); Assert.False(availability.CanOpenWorkspaceOverview);
        Assert.False(availability.CanCreateWorkspaceItem);
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.OpenWorkspaceOverview), default)).Success);
        Assert.False(Complete(host.HandleAsync(new(ChatWebHostAction.OpenFocus), default)).Success);
        Assert.NotNull(window.FindName("ChatSurface")); vm.Conversation.Dispose();
    });

    [Fact]
    public void Surface_disposal_only_detaches_projection_and_keeps_conversation_commands() => StaRunner.Run(() =>
    {
        var vm = CreateViewModel(); vm.Conversation.SetActiveServant("800100"); vm.Conversation.InputText = "保留输入";
        using var window = new DialogueWindow(vm);
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null);
        var factory = new ChatWebSurfaceFactory(new ChatWebSessionFactory(vm.Conversation),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgopet-chat-structural-" + Guid.NewGuid().ToString("N")));
        var surface = factory.CreateView(host); surface.Dispose();
        Assert.Equal(WebSurfaceState.Closed, surface.State);
        Assert.Equal("保留输入", vm.Conversation.InputText);
        Assert.True(vm.Conversation.SendCommand.CanExecute(null)); vm.Conversation.Dispose();
    });

    internal static DialogueWindowViewModel CreateViewModel(IConversationHistoryQuery? history = null, IDialogueProjectCatalog? projectCatalog = null)
    {
        var settings = new FakeSettingsStore(DialogueSettings.Defaults with
        { ModelConnection = new FgoPet.Core.Settings.ModelConnectionSettings("test", "https://example.test/v1", "test-model") });
        var database = new RuntimeDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "fgopet-chat-window-" + Guid.NewGuid().ToString("N"), "runtime.sqlite3"), pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var engine = new ConversationOrchestrator(new ThrowingProviderResolver(), new ContentResolver(),
            new SqliteConversationRepository(database), new PromptComposer(), TimeProvider.System, settings);
        return new(new ConversationViewModel(engine, settings, history: history), projectCatalog: projectCatalog);
    }
    private static WebSurfaceCommandResult Complete(ValueTask<WebSurfaceCommandResult> result)
    {
        var task = result.AsTask();
        for (var i = 0; !task.IsCompleted && i < 20; i++) StaRunner.Pump();
        Assert.True(task.IsCompleted, "The synthetic host command must complete without a real provider.");
        return task.GetAwaiter().GetResult();
    }
    private sealed class SampleProjects : IDialogueProjectCatalog
    { public Task<DialogueProjectCatalogResult> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DialogueProjectCatalogResult(true, [new("project-1", "验收项目", "", true)])); }
    private sealed class ClipboardStub : IClipboardWriter
    { public string? Text; public bool Throw; public void SetText(string text) { if (Throw) throw new ExternalException(); Text = text; } }
    private sealed class FakeSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    { public DialogueSettings Load() => initial; public void Save(DialogueSettings settings) { } }
    private sealed class ThrowingProviderResolver : IChatProviderResolver
    { public IChatProvider Resolve() => throw new FgoPet.Infrastructure.Providers.ProviderRequestException(FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。"); }
    private sealed class ContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(new ContentBinding(
            new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty), null,
            Array.Empty<KnowledgeEntry>(), Array.Empty<string>(), string.Empty, string.Empty));
    }
    private sealed class SampleWorkspaces : IWorkspaceCatalog
    {
        public IReadOnlyList<FgoPet.Extensibility.WorkspaceDescriptor> Workspaces { get; } = [new("sample.calendar", "日历"), new("sample.notes", "笔记")];
        public Dictionary<string, SampleSurface> Views { get; } = new(); public int Created;
        public FrameworkElement CreateView(string id) { Created++; return Views[id] = new SampleSurface(); }
    }
    private sealed class SampleSurface : TextBox, IWorkspaceSurface
    { public WorkspaceNavigation? Navigation; public void Navigate(WorkspaceNavigation navigation) => Navigation = navigation; public void Dispose() { } }
}
