using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using FgoPet.UiSdk;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatWorkspaceSelectionTests(ITestOutputHelper output)
{
    private readonly string _correlation = Guid.NewGuid().ToString("N");
    private void Trace(string stage, string result) => output.WriteLine(JsonSerializer.Serialize(new
        { atUtc = DateTimeOffset.UtcNow, correlation = _correlation, stage, result }));
    [Fact]
    public async Task Empty_chat_binds_only_after_persistence_and_revoke_prevents_file_access()
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            using var conversation = vm.Conversation;
            conversation.SetActiveServant("stub");
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "margin-workspace-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "synthetic");
            var registry = new WorkspaceAuthorizationRegistry(scope => scope.ConversationId == conversation.CurrentConversationId
                && scope.RoleId == conversation.ActiveServantId && scope.ProjectId == conversation.SessionContext.ProjectId);
            using var selection = new ChatWorkspaceSelection(conversation, registry);
            Assert.True(selection.Choose(() => root));
            Assert.NotEmpty(conversation.SessionContext.ProjectId);
            Assert.Equal(Path.GetFileName(root), conversation.SessionContext.ProjectLabel);
            Assert.Null(registry.GetCurrent(new("", "stub", null)));
            conversation.InputText = "synthetic request";
            conversation.RequestStatusText = "正在准备请求…";
            await conversation.SendCommand.ExecuteAsync(null);
            Assert.NotEmpty(conversation.CurrentConversationId);
            var scope = new ToolScope(conversation.CurrentConversationId, "stub", conversation.SessionContext.ProjectId);
            Assert.NotNull(registry.GetCurrent(scope));
            var guard = new WorkspaceAccessGuard(registry);
            using var lease = guard.Open(scope, "fixture.txt", WorkspacePathKind.File);
            Assert.Throws<WorkspaceAccessException>(() => guard.Open(scope, "../outside.txt", WorkspacePathKind.File));
            selection.Clear();
            Assert.Null(selection.Label);
            Assert.Empty(conversation.SessionContext.ProjectId);
            Assert.Null(registry.GetCurrent(scope));
            Assert.Throws<WorkspaceAccessException>(lease.Revalidate);
        });
    }

    [Theory]
    [InlineData("new")]
    [InlineData("role")]
    [InlineData("project")]
    [InlineData("history")]
    public async Task Session_transition_revokes_grant_and_does_not_inherit_pending_selection(string transition)
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            using var conversation = vm.Conversation;
            conversation.SetActiveServant("stub");
            var registry = new WorkspaceAuthorizationRegistry(_ => true);
            using var selection = new ChatWorkspaceSelection(conversation, registry);
            Assert.True(selection.Choose(Path.GetTempPath));
            conversation.InputText = "synthetic request";
            await conversation.SendCommand.ExecuteAsync(null);
            var scope = new ToolScope(conversation.CurrentConversationId, "stub", conversation.SessionContext.ProjectId);
            Assert.NotNull(registry.GetCurrent(scope));
            if (transition == "new") conversation.NewConversationCommand.Execute(null);
            else if (transition == "role") conversation.SetActiveServant("another-role");
            else if (transition == "project") conversation.SessionContext.TrySetProject("project", "Synthetic project");
            else conversation.SetActiveConversation(conversation.CurrentConversationId);
            Assert.Null(selection.Label);
            Assert.Null(registry.GetCurrent(scope));
            if (transition != "history")
            {
                Assert.True(selection.Choose(Path.GetTempPath));
                conversation.NewConversationCommand.Execute(null);
                Assert.Null(selection.Label);
            }
        });
    }

    [Fact]
    public async Task Native_picker_cancel_invalid_stale_hidden_and_streaming_do_not_change_authority() => await StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        using var selection = new ChatWorkspaceSelection(conversation, registry);
        Assert.True(selection.Choose(Path.GetTempPath));
        var label = selection.Label;
        Assert.True(selection.Choose(() => null));
        Assert.Equal(label, selection.Label);
        Assert.False(selection.Choose(() => "relative"));
        Assert.Equal(label, selection.Label);
        using var window = new DialogueWindow(vm);
        var picks = 0;
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null,
            selection, () => { picks++; return Path.GetTempPath(); });
        Assert.False((await host.HandleAsync(new(ChatWebHostAction.ChooseWorkspace), default)).Success);
        Assert.Equal(0, picks);
        window.Show();
        Assert.True((await host.HandleAsync(new(ChatWebHostAction.ChooseWorkspace), default)).Success);
        Assert.Equal(1, picks);
        window.Hide(); window.Show();
        Assert.Equal(label, selection.Label);
        conversation.IsStreaming = true;
        Assert.False((await host.HandleAsync(new(ChatWebHostAction.ChooseWorkspace), default)).Success);
        Assert.False((await host.HandleAsync(new(ChatWebHostAction.ClearWorkspace), default)).Success);
        Assert.Equal(1, picks);
        conversation.IsStreaming = false;
        Assert.False(selection.Choose(() => { conversation.NewConversationCommand.Execute(null); return Path.GetTempPath(); }));
        Assert.Null(selection.Label);
    });

    [Fact]
    public async Task Changing_folder_revokes_old_scope_and_binds_new_project_only_after_send() => await StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(providerResolver: new OfflineResolver());
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "project-choice-" + Guid.NewGuid().ToString("N"))).FullName;
        var first = Directory.CreateDirectory(Path.Combine(root, "first", "same-name")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second", "same-name")).FullName;
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        using var selection = new ChatWorkspaceSelection(conversation, registry);
        Assert.True(selection.Choose(() => first));
        var firstProject = conversation.SessionContext.ProjectId;
        Assert.NotEmpty(firstProject);
        conversation.InputText = "synthetic first project";
        await conversation.SendCommand.ExecuteAsync(null);
        var oldScope = new ToolScope(conversation.CurrentConversationId, "stub", firstProject);
        Assert.Equal(first, registry.GetCurrent(oldScope)?.RootPath);

        Assert.True(selection.Choose(() => second));
        Assert.NotEqual(firstProject, conversation.SessionContext.ProjectId);
        Assert.Equal("same-name", conversation.SessionContext.ProjectLabel);
        Assert.Null(registry.GetCurrent(oldScope));
        Assert.Empty(conversation.CurrentConversationId);
        Assert.Equal("same-name", selection.Label);
        conversation.InputText = "synthetic second project";
        Assert.True(conversation.CanSend);
        await conversation.SendCommand.ExecuteAsync(null);
        var newScope = new ToolScope(conversation.CurrentConversationId, "stub", conversation.SessionContext.ProjectId);
        Assert.NotEqual(oldScope.ConversationId, newScope.ConversationId);
        Assert.Equal(second, registry.GetCurrent(newScope)?.RootPath);

        Assert.True(selection.Choose(() => first + Path.DirectorySeparatorChar));
        Assert.Equal(firstProject, conversation.SessionContext.ProjectId);
        Assert.Null(registry.GetCurrent(newScope));
    });

    [Fact]
    public async Task Cancel_or_invalid_folder_preserves_project_conversation_and_live_grant() => await StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        using var selection = new ChatWorkspaceSelection(conversation, registry);
        Assert.True(selection.Choose(Path.GetTempPath));
        Assert.NotEmpty(conversation.SessionContext.ProjectId);
        conversation.InputText = "synthetic request";
        await conversation.SendCommand.ExecuteAsync(null);
        var scope = new ToolScope(conversation.CurrentConversationId, "stub", conversation.SessionContext.ProjectId);
        var grant = registry.GetCurrent(scope);
        Assert.NotNull(grant);
        Assert.True(selection.Choose(() => null));
        Assert.False(selection.Choose(() => "relative"));
        Assert.Equal(scope.ProjectId, conversation.SessionContext.ProjectId);
        Assert.Equal(scope.ConversationId, conversation.CurrentConversationId);
        Assert.Same(grant, registry.GetCurrent(scope));
        Assert.True(selection.Choose(Path.GetTempPath));
        Assert.Equal(scope.ConversationId, conversation.CurrentConversationId);
    });

    [Fact]
    public async Task Folder_selection_and_removal_preserve_unsent_draft() => await StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        using var selection = new ChatWorkspaceSelection(conversation, new WorkspaceAuthorizationRegistry(_ => true));
        conversation.InputText = "synthetic unsent draft";
        Assert.True(selection.Choose(Path.GetTempPath));
        Assert.Equal("synthetic unsent draft", conversation.InputText);
        selection.Clear();
        Assert.Empty(conversation.SessionContext.ProjectId);
        Assert.Equal("synthetic unsent draft", conversation.InputText);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task History_restores_project_but_requires_folder_reselection_for_file_access() => await StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        using var selection = new ChatWorkspaceSelection(conversation, registry);
        Assert.True(selection.Choose(Path.GetTempPath));
        conversation.InputText = "synthetic request";
        await conversation.SendCommand.ExecuteAsync(null);
        var scope = new ToolScope(conversation.CurrentConversationId, "stub", conversation.SessionContext.ProjectId);
        Assert.NotNull(registry.GetCurrent(scope));
        conversation.SetActiveConversation(scope.ConversationId);
        Assert.Equal(scope.ProjectId, conversation.SessionContext.ProjectId);
        Assert.Null(selection.Label);
        Assert.Null(registry.GetCurrent(scope));
        Assert.True(selection.Choose(Path.GetTempPath));
        Assert.Equal(scope.ConversationId, conversation.CurrentConversationId);
        Assert.NotNull(registry.GetCurrent(scope));
        selection.Dispose();
        Assert.Null(registry.GetCurrent(scope));
        Assert.Equal(scope.ProjectId, conversation.SessionContext.ProjectId);
    });

    [Theory]
    [InlineData("clearWorkspace")]
    [InlineData("removeContextChip")]
    public async Task Native_bridge_folder_choice_and_removal_keep_identity_draft_and_authority_in_sync(string removal) => await StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(providerResolver: new OfflineResolver());
        using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        using var selection = new ChatWorkspaceSelection(conversation, registry);
        using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null, selection, Path.GetTempPath);
        using var session = new ChatWebSession(conversation, host);
        WebSurfaceMessage Command(string action)
        {
            var identity = session.ReadSnapshot().Identity;
            var payload = new Dictionary<string, object?> { ["sessionId"] = identity.SessionId,
                ["servantId"] = identity.ServantId, ["conversationId"] = identity.ConversationId, ["action"] = action };
            if (action == "removeContextChip") payload["targetId"] = "project";
            return new("chat.host", Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload));
        }
        window.Show();
        Trace("folder.choice.received", "started");
        conversation.InputText = "synthetic draft";
        Assert.True((await session.HandleCommandAsync(Command("chooseWorkspace"), default)).Success);
        var chosen = session.ReadSnapshot();
        Assert.NotEmpty(chosen.Conversation.Project.Id);
        Assert.Equal(chosen.Conversation.Project.Label, chosen.Presentation.WorkspaceLabel);
        Assert.Equal("synthetic draft", chosen.Draft.Text);
        Assert.Empty(chosen.Presentation.Projects);
        Trace("folder.choice.bound", "passed");
        await conversation.SendCommand.ExecuteAsync(null);
        var scope = new ToolScope(conversation.CurrentConversationId, "stub", chosen.Conversation.Project.Id);
        Assert.NotNull(registry.GetCurrent(scope));
        Trace("conversation.persisted.grant", "passed");
        conversation.InputText = "synthetic next draft";
        Assert.True((await session.HandleCommandAsync(Command(removal), default)).Success);
        var cleared = session.ReadSnapshot();
        Assert.Empty(cleared.Conversation.Project.Id);
        Assert.Null(cleared.Presentation.WorkspaceLabel);
        Assert.Null(registry.GetCurrent(scope));
        Assert.Equal("synthetic next draft", cleared.Draft.Text);
        Trace("folder.removed.grant_revoked", "passed");
    });

    private sealed class ClipboardStub : IClipboardWriter { public void SetText(string text) { } }
    private sealed class OfflineResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() => new OfflineProvider();
    }
    private sealed class OfflineProvider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "test-model";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new("""{"text":"Synthetic reply","emotion":"neutral"}""", IsComplete: true, FinishReason: "stop");
        }
    }
}
