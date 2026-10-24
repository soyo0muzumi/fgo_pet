using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatImageSessionReliabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Replayed_first_chunk_cannot_replace_newer_buffer_or_cancel_newer_import(bool newerComplete) => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var host = new DelayedImageHost(); using var session = new ChatWebSession(conversation, host);
        var oldId = Guid.NewGuid().ToString("N"); var currentId = Guid.NewGuid().ToString("N");
        var first = session.HandleCommandAsync(Chunk(session, oldId), default).AsTask();
        var current = session.HandleCommandAsync(Chunk(session, currentId, complete: newerComplete), default).AsTask();
        if (!newerComplete) Assert.True((await current).Success);

        var replay = await session.HandleCommandAsync(Chunk(session, oldId, complete: false), default);
        Assert.False(replay.Success);
        Assert.Equal("CHAT_IMAGE_BUSY", (await session.HandleCommandAsync(Command(session, "chat.send"), default)).ErrorCode);
        if (!newerComplete) current = session.HandleCommandAsync(Chunk(session, currentId, offset: 3), default).AsTask();
        Assert.Equal(2, host.Pending.Count);
        Assert.False(host.Pending[1].Token.IsCancellationRequested);

        host.Pending[0].Completion.SetResult(new(true));
        Assert.False((await first).Success);
        host.Pending[1].Completion.SetResult(new(true));
        Assert.True((await current).Success);
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("conversation")]
    [InlineData("role")]
    [InlineData("project")]
    [InlineData("model")]
    [InlineData("model-status")]
    [InlineData("provider-status")]
    [InlineData("stop")]
    [InlineData("native-stop")]
    [InlineData("clear")]
    [InlineData("picker")]
    [InlineData("dispose")]
    public Task Invalidation_cancels_import_and_rejects_late_success(string change) => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var host = new DelayedImageHost(); using var session = new ChatWebSession(conversation, host);
        var import = session.HandleCommandAsync(Chunk(session), default).AsTask();
        var pending = Assert.Single(host.Pending);
        Assert.False(import.IsCompleted);

        switch (change)
        {
            case "cancel": await session.HandleCommandAsync(Command(session, "chat.image.cancel"), default); break;
            case "conversation": conversation.NewConversationCommand.Execute(null); break;
            case "role": conversation.SetActiveServant("other"); break;
            case "project": conversation.SessionContext.TrySetProject("other-project", "Synthetic project"); break;
            case "model": await session.HandleCommandAsync(Command(session, "chat.host", "selectModel", "other-model"), default); break;
            case "model-status": conversation.ModelStatusText = "different-model"; break;
            case "provider-status": conversation.ProviderStatusText = "different-provider"; break;
            case "stop": await session.HandleCommandAsync(Command(session, "chat.stop"), default); break;
            case "native-stop": conversation.StopCommand.Execute(null); break;
            case "clear": await session.HandleCommandAsync(Command(session, "chat.host", "clearImage"), default); break;
            case "picker": await session.HandleCommandAsync(Command(session, "chat.host", "chooseImage"), default); break;
            case "dispose": session.Dispose(); break;
        }

        // Deliberately release an importer that ignored cancellation. The session
        // must reject its result independently of cooperative cancellation.
        pending.Completion.SetResult(new(true));
        Assert.False((await import).Success);
        Assert.True(pending.Token.IsCancellationRequested);
    });

    [Fact]
    public Task Superseded_import_cannot_release_send_gate_owned_by_newer_import() => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub"); conversation.InputText = "synthetic draft";
        var host = new DelayedImageHost(); using var session = new ChatWebSession(conversation, host);
        var first = session.HandleCommandAsync(Chunk(session), default).AsTask();
        var second = session.HandleCommandAsync(Chunk(session), default).AsTask();
        Assert.Equal(2, host.Pending.Count);

        host.Pending[0].Completion.SetResult(new(true));
        Assert.False((await first).Success);
        Assert.True(host.Pending[0].Token.IsCancellationRequested);
        Assert.Equal("CHAT_IMAGE_BUSY", (await session.HandleCommandAsync(Command(session, "chat.send"), default)).ErrorCode);
        Assert.False(second.IsCompleted);

        host.Pending[1].Completion.SetResult(new(true));
        Assert.True((await second).Success);
    });

    [Fact]
    public Task Failed_import_releases_send_gate_and_allows_new_import() => StaRunner.RunAsync(async () =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel(); using var conversation = vm.Conversation;
        conversation.SetActiveServant("stub");
        var host = new DelayedImageHost(); using var session = new ChatWebSession(conversation, host);
        var failed = session.HandleCommandAsync(Chunk(session), default).AsTask();
        Assert.Equal("CHAT_IMAGE_BUSY", (await session.HandleCommandAsync(Command(session, "chat.send"), default)).ErrorCode);
        host.Pending[0].Completion.SetResult(new(false, ErrorCode: "CHAT_IMAGE_INVALID"));
        Assert.False((await failed).Success);

        var retry = session.HandleCommandAsync(Chunk(session), default).AsTask();
        host.Pending[1].Completion.SetResult(new(true));
        Assert.True((await retry).Success);
        Assert.NotEqual("CHAT_IMAGE_BUSY", (await session.HandleCommandAsync(Command(session, "chat.send"), default)).ErrorCode);
    });

    private static WebSurfaceMessage Chunk(ChatWebSession session, string? transferId = null, int offset = 0, bool complete = true)
    {
        var payload = Identity(session);
        payload["transferId"] = transferId ?? Guid.NewGuid().ToString("N");
        payload["mimeType"] = "image/png"; payload["offset"] = offset;
        payload["data"] = "AQID"; payload["complete"] = complete;
        return Message("chat.image", payload);
    }

    private static WebSurfaceMessage Command(ChatWebSession session, string type, string? action = null, string? target = null)
    {
        var payload = Identity(session);
        if (action is not null) payload["action"] = action;
        if (target is not null) payload["targetId"] = target;
        return Message(type, payload);
    }

    private static Dictionary<string, object?> Identity(ChatWebSession session)
    {
        var identity = session.ReadSnapshot().Identity;
        return new() { ["sessionId"] = identity.SessionId, ["servantId"] = identity.ServantId, ["conversationId"] = identity.ConversationId };
    }

    private static WebSurfaceMessage Message(string type, object payload) =>
        new(type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload));

    private sealed record PendingImport(CancellationToken Token, TaskCompletionSource<WebSurfaceCommandResult> Completion);

    private sealed class DelayedImageHost : IChatWebHostActions
    {
        public List<PendingImport> Pending { get; } = [];
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic role", null, [], [new("other-model", "Other model", false)],
            Actions: new(CanChooseImage: true, CanClearImage: true));
        public ValueTask<WebSurfaceCommandResult> ImportImageAsync(byte[] bytes, string mimeType, CancellationToken token)
        {
            var completion = new TaskCompletionSource<WebSurfaceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(new(token, completion));
            return new(completion.Task);
        }
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken token) =>
            ValueTask.FromResult(new WebSurfaceCommandResult(true));
    }
}
