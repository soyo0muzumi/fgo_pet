using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatWorkspaceSelectionTests
{
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
                && scope.RoleId == conversation.ActiveServantId && scope.ProjectId == null);
            using var selection = new ChatWorkspaceSelection(conversation, registry);
            Assert.True(selection.Choose(() => root));
            Assert.Null(registry.GetCurrent(new("", "stub", null)));
            conversation.InputText = "synthetic request";
            conversation.RequestStatusText = "正在准备请求…";
            await conversation.SendCommand.ExecuteAsync(null);
            Assert.NotEmpty(conversation.CurrentConversationId);
            var scope = new ToolScope(conversation.CurrentConversationId, "stub", null);
            Assert.NotNull(registry.GetCurrent(scope));
            var guard = new WorkspaceAccessGuard(registry);
            using var lease = guard.Open(scope, "fixture.txt", WorkspacePathKind.File);
            Assert.Throws<WorkspaceAccessException>(() => guard.Open(scope, "../outside.txt", WorkspacePathKind.File));
            selection.Clear();
            Assert.Null(selection.Label);
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
            var scope = new ToolScope(conversation.CurrentConversationId, "stub", null);
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

    private sealed class ClipboardStub : IClipboardWriter { public void SetText(string text) { } }
}
