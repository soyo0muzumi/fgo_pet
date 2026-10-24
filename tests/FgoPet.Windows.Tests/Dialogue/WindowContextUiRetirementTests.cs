using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Platform.WindowText;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class WindowContextUiRetirementTests
{
    [Theory]
    [InlineData(ChatWebHostAction.ChooseWindowContext)]
    [InlineData(ChatWebHostAction.PreviewWindowContext)]
    [InlineData(ChatWebHostAction.RereadWindowContext)]
    [InlineData(ChatWebHostAction.ClearWindowContext)]
    public Task Legacy_window_actions_are_unavailable_and_leave_backend_content_untouched(ChatWebHostAction action) =>
        StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            using var conversation = vm.Conversation;
            conversation.SetActiveServant("synthetic-role");
            var reader = new Reader();
            using var selection = new ChatWindowSelection(reader, conversation.WindowContextScopeKey);
            conversation.BindWindowSelection(selection);
            await selection.PreviewAsync("synthetic-token", default);
            Assert.True(selection.AttachPreview());
            var attached = selection.Current;
            using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
            using var host = new ChatWebHostActions(vm, window, new ClipboardStub(), null, null, null);
            window.Show();
            var presentation = host.ReadPresentation();
            Assert.Null(presentation.WindowContext);
            var availability = Assert.IsType<ChatWebHostAvailability>(presentation.Actions);
            Assert.False(availability.CanChooseWindowContext);
            Assert.False(availability.CanPreviewWindowContext);
            Assert.False(availability.CanRereadWindowContext);
            Assert.False(availability.CanClearWindowContext);
            var result = await host.HandleAsync(new(action), default);
            Assert.False(result.Success);
            Assert.Equal("CHAT_TARGET_UNAVAILABLE", result.ErrorCode);
            Assert.Empty(window.OwnedWindows.Cast<System.Windows.Window>());
            Assert.Equal(0, reader.ListCalls);
            Assert.Equal(1, reader.ReadCalls);
            Assert.Same(attached, selection.Current);
            window.Hide();
        });

    private sealed class ClipboardStub : IClipboardWriter { public void SetText(string text) { } }

    private sealed class Reader : IWindowTextReader
    {
        public int ListCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public IReadOnlyList<WindowTextCandidate> ListCandidates() { ListCalls++; return []; }
        public Task<WindowTextSnapshot> ReadAsync(string candidateToken, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(new WindowTextSnapshot("synthetic window", "fixture", "synthetic text",
                DateTimeOffset.UnixEpoch, WindowTextReadStatus.Complete));
        }
    }
}
