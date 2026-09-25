using System.IO;
using System.Reflection;
using System.Threading;
using FgoPet.App.Dialogue;
using FgoPet.App.Speech;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Speech;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

[Trait("Category", "WindowsIntegration")]
public sealed class DialogueSpeechBoundaryTests
{
    [Fact]
    public void Constructor_field_and_public_property_expose_only_the_configured_playback_port()
    {
        var type = typeof(DialogueWindowViewModel);
        var parameter = Assert.Single(Assert.Single(type.GetConstructors()).GetParameters().Where(item => item.Name == "speech"));
        Assert.Equal(typeof(IConfiguredSpeechPlayback), parameter.ParameterType);
        Assert.Equal(typeof(IConfiguredSpeechPlayback), type.GetProperty("Speech")!.PropertyType);
        Assert.Equal(typeof(IConfiguredSpeechPlayback), type.GetField("_speech", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType);
        Assert.Same(typeof(SpeechPlaybackState).Assembly, typeof(SpeechPlaybackResult).Assembly);
    }

    [Fact]
    public Task Worker_state_changes_are_applied_only_on_the_owning_dispatcher() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var turn = NewTurn("a");
        var work = fixture.Model.ReadAloudAsync(turn);
        var threadIds = new List<int>();
        var ownerThread = Environment.CurrentManagedThreadId;
        turn.PropertyChanged += (_, _) => threadIds.Add(Environment.CurrentManagedThreadId);

        RaiseOnWorker(fixture.Port, SpeechPlaybackState.Playing);
        Assert.Equal("准备朗读…", turn.SpeechStatusText);
        Assert.Empty(threadIds);
        StaRunner.Pump();
        Assert.Equal("正在朗读…", turn.SpeechStatusText);
        Assert.NotEmpty(threadIds);
        Assert.All(threadIds, id => Assert.Equal(ownerThread, id));
        fixture.Port.Calls[0].Completion.SetResult(new(true, SpeechPlaybackState.Ended));
        await work;
        Assert.False(turn.IsSpeechBusy);
    });

    [Fact]
    public Task Queued_state_and_late_result_from_the_previous_turn_cannot_change_the_next_turn() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var first = NewTurn("first");
        var second = NewTurn("second");
        var oldWork = fixture.Model.ReadAloudAsync(first);
        RaiseOnWorker(fixture.Port, SpeechPlaybackState.Playing);
        var currentWork = fixture.Model.ReadAloudAsync(second);
        StaRunner.Pump();
        Assert.False(first.IsSpeechBusy);
        Assert.Equal("准备朗读…", second.SpeechStatusText);
        fixture.Port.Calls[0].Completion.SetResult(new(false, SpeechPlaybackState.Failed, "旧请求失败"));
        await oldWork;
        Assert.True(second.IsSpeechBusy);
        Assert.Equal("准备朗读…", second.SpeechStatusText);
        fixture.Port.Calls[1].Completion.SetResult(new(true, SpeechPlaybackState.Ended));
        await currentWork;
        Assert.False(second.IsSpeechBusy);
        Assert.Empty(second.SpeechStatusText);
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("deactivate")]
    [InlineData("session")]
    [InlineData("servant")]
    public Task Stopping_or_changing_context_invalidates_pending_speech_presentation(string action) => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var turn = NewTurn("a");
        var work = fixture.Model.ReadAloudAsync(turn);
        RaiseOnWorker(fixture.Port, SpeechPlaybackState.Playing);
        switch (action)
        {
            case "hide": fixture.Model.NotifyWindowHidden(); break;
            case "deactivate": fixture.Model.NotifyDeactivated(); break;
            case "session": fixture.Conversation.NewConversationCommand.Execute(null); break;
            case "servant": fixture.Conversation.ActiveServantId = "another-servant"; break;
        }
        StaRunner.Pump();
        fixture.Port.Calls[0].Completion.SetResult(new(false, SpeechPlaybackState.Failed, "迟到的错误"));
        await work;
        Assert.True(fixture.Port.Stops > 0);
        Assert.False(turn.IsSpeechBusy);
        Assert.False(turn.SpeechNeedsConfiguration);
        Assert.Empty(turn.SpeechStatusText);
    });

    [Fact]
    public Task Repeated_manual_read_on_the_active_message_stops_instead_of_starting_another_request() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var turn = NewTurn("a");
        var work = fixture.Model.ReadAloudAsync(turn);
        var stop = await fixture.Model.ReadAloudAsync(turn);
        Assert.NotNull(stop);
        Assert.Equal(SpeechPlaybackState.Stopped, stop!.State);
        Assert.Single(fixture.Port.Calls);
        Assert.Equal(1, fixture.Port.Stops);
        fixture.Port.Calls[0].Completion.SetResult(new(true, SpeechPlaybackState.Ended));
        await work;
        Assert.False(turn.IsSpeechBusy);
        Assert.Empty(turn.SpeechStatusText);
    });

    [Fact]
    public Task Disposal_detaches_notifications_without_disposing_the_shared_service_or_stopping_chat() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var turn = NewTurn("a");
        var work = fixture.Model.ReadAloudAsync(turn);
        RaiseOnWorker(fixture.Port, SpeechPlaybackState.Playing);
        Assert.Equal(1, fixture.Port.Subscribers);
        fixture.Model.Dispose();
        fixture.Model.Dispose();
        Assert.Equal(0, fixture.Port.Subscribers);
        Assert.Equal(1, fixture.Port.Stops);
        Assert.False(fixture.Port.Disposed);
        StaRunner.Pump();
        fixture.Port.Calls[0].Completion.SetResult(new(false, SpeechPlaybackState.Failed, "迟到的错误"));
        await work;
        Assert.Null(await fixture.Model.ReadAloudAsync(NewTurn("ignored")));
        Assert.Empty(turn.SpeechStatusText);
        fixture.Model.NotifyActivated();
        await fixture.SendAsync();
        Assert.Single(fixture.Port.Calls);
        Assert.Contains(fixture.Conversation.Turns, item => item.IsAssistant && item.Text == "聊天正常。");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Completed_replies_keep_the_existing_active_window_auto_read_rule(bool active) => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Port.ImmediateResult = new(true, SpeechPlaybackState.Ended);
        if (active) fixture.Model.NotifyActivated();
        await fixture.SendAsync();
        if (active)
        {
            var call = Assert.Single(fixture.Port.Calls);
            Assert.True(call.AutoRead);
            Assert.Equal("聊天正常。", call.Text);
        }
        else Assert.Empty(fixture.Port.Calls);
        Assert.Empty(fixture.Conversation.ErrorText);
    });

    [Fact]
    public Task Configuration_failure_and_retry_leave_text_chat_available() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Port.ImmediateResult = new(false, SpeechPlaybackState.Failed, "请检查朗读设置。");
        var turn = NewTurn("a");
        await fixture.Model.ReadAloudAsync(turn);
        Assert.True(turn.SpeechNeedsConfiguration);
        Assert.Equal("去朗读设置", turn.SpeechActionText);
        await fixture.SendAsync();
        Assert.Empty(fixture.Conversation.ErrorText);
        fixture.Port.ImmediateResult = new(true, SpeechPlaybackState.Ended);
        await fixture.Model.ReadAloudAsync(turn);
        Assert.False(turn.SpeechNeedsConfiguration);
        Assert.False(turn.IsSpeechBusy);
        Assert.Equal(2, fixture.Port.Calls.Count);
        Assert.All(fixture.Port.Calls, call => Assert.False(call.AutoRead));
    });

    [Fact]
    public Task Hiding_and_reopening_reuses_one_subscription_and_the_same_port() => StaRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Port.ImmediateResult = new(true, SpeechPlaybackState.Ended);
        fixture.Model.NotifyWindowHidden();
        fixture.Model.NotifyActivated();
        Assert.Equal(1, fixture.Port.Subscribers);
        Assert.Same(fixture.Port, fixture.Model.Speech);
        await fixture.Model.ReadAloudAsync(NewTurn("a"));
        Assert.Single(fixture.Port.Calls);
    });

    private static ConversationTurnViewModel NewTurn(string id) => new(id, ChatMessageRole.Assistant, "朗读正文。");

    private static void RaiseOnWorker(Playback port, SpeechPlaybackState state)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { port.Raise(state); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "State notification must post without waiting for the UI thread.");
        Assert.Null(failure);
    }

    private sealed record Call(string? Text, bool AutoRead, TaskCompletionSource<SpeechPlaybackResult> Completion);
    private sealed class Playback : IConfiguredSpeechPlayback, IDisposable
    {
        private EventHandler? _handlers;
        public event EventHandler? StateChanged { add => _handlers += value; remove => _handlers -= value; }
        public int Subscribers => _handlers?.GetInvocationList().Length ?? 0;
        public SpeechPlaybackState State { get; private set; }
        public List<Call> Calls { get; } = [];
        public SpeechPlaybackResult? ImmediateResult { get; set; }
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public Task<SpeechPlaybackResult> PlayConfiguredAsync(string? text, bool autoRead = false, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<SpeechPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new(text, autoRead, completion));
            if (ImmediateResult is { } result) completion.SetResult(result);
            return completion.Task;
        }
        public void Raise(SpeechPlaybackState state) { State = state; _handlers?.Invoke(this, EventArgs.Empty); }
        public void Stop() => Stops++;
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "fgo-speech-port-" + Guid.NewGuid().ToString("N") + ".db");
        public Playback Port { get; } = new();
        public ConversationViewModel Conversation { get; }
        public DialogueWindowViewModel Model { get; }
        public Fixture()
        {
            var database = new RuntimeDatabase(_path, pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var settings = new Settings();
            var orchestrator = new ConversationOrchestrator(new Resolver(), new Content(), new SqliteConversationRepository(database),
                new NoMemory(), new PromptComposer(), TimeProvider.System, settings: settings);
            Conversation = new ConversationViewModel(orchestrator, settings) { ActiveServantId = "mash" };
            Model = new DialogueWindowViewModel(Conversation, speech: Port);
        }
        public async Task SendAsync()
        {
            Conversation.InputText = "你好";
            await Conversation.SendCommand.ExecuteAsync(null);
            Assert.False(Conversation.IsStreaming);
        }
        public void Dispose()
        {
            Model.Dispose();
            Conversation.Dispose();
            foreach (var call in Port.Calls) call.Completion.TrySetResult(new(false, SpeechPlaybackState.Stopped));
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        private DialogueSettings _value = DialogueSettings.Defaults with
        {
            ModelConnection = new("test", "https://fixture.test", "model", contextWindowOverride: 32768),
        };
        public DialogueSettings Load() => _value;
        public void Save(DialogueSettings settings) => _value = settings;
    }
    private sealed class NoMemory : IMemoryRecall
    {
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) => new(0, []);
    }
    private sealed class Resolver : IChatProviderResolver
    {
        public IChatProvider Resolve() => new Provider();
    }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(new("mash", "test", "1", "default", "1", "1"),
                new PersonaBundle("mash", "test", "1", "1", "认真回应。", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "model";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new("""{"text":"聊天正常。","emotion":"neutral"}""", IsComplete: true, FinishReason: "stop");
        }
    }
}
