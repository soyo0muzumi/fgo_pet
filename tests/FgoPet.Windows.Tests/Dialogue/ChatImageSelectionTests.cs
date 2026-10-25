using System.ComponentModel;
using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatImageSelectionTests
{
    [Theory]
    [InlineData("role")]
    [InlineData("conversation")]
    [InlineData("project")]
    [InlineData("model")]
    [InlineData("stop")]
    public Task Picker_result_cannot_cross_scope_or_route_change(string change) => StaRunner.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "image-scope-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        try
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
            conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true); var settings = new Settings();
            using var selection = new ChatImageSelection(conversation, images, settings, TimeProvider.System);
            Assert.False(await selection.ChooseAsync(() =>
            {
                if (change == "role") conversation.SetActiveServant("other");
                if (change == "conversation") conversation.NewConversationCommand.Execute(null);
                if (change == "project") conversation.SessionContext.TrySetProject("other-project", "Synthetic project");
                if (change == "model") settings.Connection = new("deepseek", "https://api.deepseek.com/v1", "deepseek-flash");
                if (change == "stop") conversation.StopCommand.Execute(null);
                return path;
            }, default));
            Assert.Null(selection.Label);
            Assert.False(images.HasSelection);
            Assert.True(selection.CanChoose);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public Task Cancelled_import_and_picker_preserve_previous_unshared_selection() => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true);
        using var selection = new ChatImageSelection(conversation, images, new Settings(), TimeProvider.System);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Assert.True(await selection.ImportAsync(png, "image/png", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection.ImportAsync(png, "image/png", cancelled.Token));
        Assert.True(await selection.ChooseAsync(() => null, default));
        Assert.NotNull(selection.Label);
        Assert.False(images.HasSelection);
        conversation.NewConversationCommand.Execute(null);
        Assert.Null(selection.Label);
        Assert.True(await selection.ImportAsync(png, "image/png", default));
        Assert.False(images.HasSelection);
    });

    [Fact]
    public Task Cancellation_while_picker_is_open_is_not_reported_as_success() => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true);
        using var selection = new ChatImageSelection(conversation, images, new Settings(), TimeProvider.System);
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection.ChooseAsync(() =>
        {
            cancelled.Cancel();
            return null;
        }, cancelled.Token));

        Assert.Null(selection.Label);
        Assert.True(selection.CanChoose);
    });

    [Theory]
    [InlineData("role")]
    [InlineData("conversation")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public Task Selection_change_observer_cannot_resurrect_image_after_invalidation(string change) => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true);
        using var selection = new ChatImageSelection(conversation, images, new Settings(), TimeProvider.System);
        var observed = false;
        selection.Changed += (_, _) =>
        {
            if (observed) return;
            observed = true;
            if (change == "role") conversation.SetActiveServant("different");
            if (change == "conversation") conversation.NewConversationCommand.Execute(null);
            if (change == "stop") conversation.StopCommand.Execute(null);
            if (change == "dispose") selection.Dispose();
        };

        await selection.ImportAsync(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), "image/png", default);

        Assert.True(observed);
        Assert.Null(selection.Label);
        Assert.False(images.HasSelection);
    });

    [Fact]
    public Task Paste_import_validates_pixels_preserves_previous_on_failure_and_waits_for_send() => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true);
        using var selection = new ChatImageSelection(conversation, images, new Settings(), TimeProvider.System);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Assert.True(await selection.ImportAsync(png, "image/png", default));
        Assert.NotNull(selection.Label); Assert.False(images.HasSelection);
        Assert.Empty(conversation.CurrentConversationId);
        Assert.False(await selection.ImportAsync([1, 2, 3], "image/png", default));
        Assert.NotNull(selection.Label);
        Assert.False(await selection.ImportAsync(png, "image/svg+xml", default));
        conversation.IsStreaming = true;
        Assert.False(await selection.ImportAsync(png, "image/png", default));
        conversation.StopCommand.Execute(null); Assert.Null(selection.Label);
    });

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4.1-mini", "OpenAI")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", "DeepSeek")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash", "Tencent CodeBuddy CN")]
    public async Task Picker_selection_does_not_send_binds_after_persistence_and_is_revoked_at_turn_end(
        string provider, string endpoint, string model, string recipient)
    {
        await StaRunner.RunAsync(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "image-fixture-" + Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            try
            {
                var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
                conversation.SetActiveServant("stub");
                var settings = new Settings(new(provider, endpoint, model)); var route = ModelRouteKey.From(settings.Load().ModelConnection!);
                var images = new SelectedImageStore(scope => scope.ConversationId == conversation.CurrentConversationId && scope.RoleId == "stub");
                using var selection = new ChatImageSelection(conversation, images, settings, TimeProvider.System);
                Assert.True(await selection.ChooseAsync(() => path, default)); Assert.NotNull(selection.Label);
                Assert.Empty(conversation.CurrentConversationId);
                Assert.Contains("Sending shares", selection.Disclosure);
                Assert.Contains($"{model} at {recipient}", selection.Disclosure);
                Assert.Contains("Selection alone does not send", selection.Disclosure);
                if (provider == "custom-openai-compatible")
                {
                    Assert.InRange(selection.Disclosure.Length, 1, 512);
                    Assert.Contains("local Buddy", selection.Disclosure);
                    Assert.Contains("disk", selection.Disclosure);
                    Assert.Contains("re-encode", selection.Disclosure);
                    Assert.Contains("retry", selection.Disclosure);
                    Assert.Contains("switch accounts", selection.Disclosure);
                    Assert.Contains("no shared upstream request cap", selection.Disclosure);
                    Assert.Contains("cannot be recalled", selection.Disclosure);
                }
                var observedGrant = false;
                string? userMessageId = null;
                conversation.DesktopMessagePersisted += id => userMessageId = id;
                conversation.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(conversation.CurrentConversationId) && !string.IsNullOrWhiteSpace(conversation.CurrentConversationId))
                    {
                        images.Bind(new(conversation.CurrentConversationId, "stub", null), route, userMessageId!, "run", default);
                        observedGrant = images.Read(new(conversation.CurrentConversationId, "stub", null), route, "run", default) is not null;
                    }
                };
                conversation.InputText = "synthetic request"; await conversation.SendCommand.ExecuteAsync(null);
                Assert.True(observedGrant); Assert.Null(selection.Label);
                Assert.Throws<SelectedImageException>(() => images.Read(new(conversation.CurrentConversationId, "stub", null), route, "run", default));
                Assert.True(await selection.ChooseAsync(() => path, default));
                conversation.Dispose();
                Assert.Null(selection.Label);
                Assert.Throws<SelectedImageException>(() => images.Read(new(conversation.CurrentConversationId, "stub", null), route, "run", default));
            }
            finally { File.Delete(path); }
        });
    }
    [Fact]
    public async Task Picker_cancel_stale_selection_and_stop_cannot_leave_authority()
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
            conversation.SetActiveServant("stub"); var images = new SelectedImageStore(_ => true);
            using var selection = new ChatImageSelection(conversation, images, new Settings(), TimeProvider.System);
            Assert.True(await selection.ChooseAsync(() => null, default)); Assert.Null(selection.Label);
            conversation.IsStreaming = true;
            Assert.False(await selection.ChooseAsync(() => throw new InvalidOperationException("picker must not open"), default));
            conversation.StopCommand.Execute(null); Assert.Null(selection.Label);
        });
    }
    [Theory]
    [InlineData("provider")]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("expiry")]
    [InlineData("role")]
    [InlineData("cancel")]
    public async Task Buddy_picker_authority_is_revoked_on_route_scope_expiry_or_stop(string change)
    {
        await StaRunner.RunAsync(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "image-fixture-" + Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            try
            {
                var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
                conversation.SetActiveServant("stub");
                var settings = new Settings(new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"));
                var images = new SelectedImageStore(_ => true); var clock = new Clock();
                using var selection = new ChatImageSelection(conversation, images, settings, clock);
                Assert.True(await selection.ChooseAsync(() => path, default)); Assert.NotNull(selection.Label);
                if (change == "provider") settings.Connection = new("openai", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash");
                if (change == "endpoint") settings.Connection = new("custom-openai-compatible", "http://127.0.0.1:8327/v1", "buddy/deepseek-v4.1-flash");
                if (change == "model") settings.Connection = new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "other");
                if (change == "expiry") clock.Now += SelectedImageStore.Lifetime;
                if (change == "role") conversation.SetActiveServant("different");
                if (change == "cancel") { conversation.IsStreaming = true; conversation.StopCommand.Execute(null); }
                conversation.InputText = "synthetic state change";
                Assert.Null(selection.Label);
            }
            finally { File.Delete(path); }
        });
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Settings(ModelConnectionSettings? connection = null) : IDialogueSettingsStore
    {
        public ModelConnectionSettings Connection { get; set; } = connection ?? new("openai", "https://api.openai.com/v1", "gpt-4.1-mini");
        public DialogueSettings Load() => new(Connection, true);
        public void Save(DialogueSettings settings) { }
    }
}
