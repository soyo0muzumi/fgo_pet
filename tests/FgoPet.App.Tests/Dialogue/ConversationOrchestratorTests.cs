using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationOrchestratorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-phase3-orchestrator-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Send_persists_user_and_final_assistant_messages_with_context()
    {
        var provider = new FakeProvider([new ChatStreamChunk("已收到"), new ChatStreamChunk("，御主", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);

        var result = await orchestrator.SendAsync("800100", "请陪我工作", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var messages = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
        Assert.Equal(ChatMessageStatus.Completed, messages[1].Status);
        Assert.Equal("已收到，御主", messages[1].Text);
        Assert.Equal("casual", messages[1].ContentContext.AppearanceId);
    }

    [Fact]
    public async Task Cancel_does_not_persist_partial_assistant_text()
    {
        var provider = new BlockingProvider();
        var orchestrator = CreateOrchestrator(provider);
        using var cancellation = new CancellationTokenSource();
        var task = orchestrator.SendAsync("800100", "开始", cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();

        var result = await task;

        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        var messages = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.DoesNotContain(messages, message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
    }

    [Fact]
    public async Task Structured_memory_candidate_is_saved_as_pending()
    {
        var provider = new FakeProvider([new ChatStreamChunk("{\"text\":\"记住这件事。\",\"memory_candidate\":\"用户喜欢安静工作。\"}", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);

        var result = await orchestrator.SendAsync("800100", "请记住", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var candidates = CreateMemoryRepository().ListCandidates("800100");
        var candidate = Assert.Single(candidates);
        Assert.Equal(MemoryCandidateStatus.Pending, candidate.Status);
        Assert.Equal("用户喜欢安静工作。", candidate.Text);
    }

    [Fact]
    public async Task Conversation_view_model_exposes_bounded_turns_and_send_state()
    {
        var provider = new FakeProvider([new ChatStreamChunk("收到", IsComplete: true)]);
        var viewModel = new ConversationViewModel(CreateOrchestrator(provider), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "请陪我工作";

        Assert.True(viewModel.CanSend);
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStreaming);
        Assert.Equal(2, viewModel.Turns.Count);
        Assert.Equal("我", viewModel.Turns[0].RoleLabel);
        Assert.Equal("收到", viewModel.Turns[1].Text);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private ConversationOrchestrator CreateOrchestrator(IChatProvider provider)
    {
        var binding = new ContentBinding(
            new ContentContextKey("800100", "test-persona", "1.0.0", "casual", "2.1.0", "3.0.0"),
            new PersonaBundle("800100", "test-persona", "1.0.0", "2.1.0", "认真陪伴用户。", []),
            [],
            ["servant-core", "casual"],
            new string('a', 64),
            new string('b', 64));
        return new ConversationOrchestrator(
            new FakeProviderResolver(provider),
            new FakeContentResolver(binding),
            CreateConversationRepository(),
            CreateMemoryRepository(),
            new PromptComposer(),
            TimeProvider.System);
    }

    private SqliteConversationRepository CreateConversationRepository()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteConversationRepository(database);
    }

    private SqliteMemoryRepository CreateMemoryRepository()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteMemoryRepository(database);
    }

    private sealed class FakeProviderResolver(IChatProvider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }

    private sealed class FakeContentResolver(ContentBinding binding) : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(binding);
        }
    }

    private sealed class FakeProvider(IReadOnlyList<ChatStreamChunk> chunks) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([new ProviderModel(ModelId)]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class BlockingProvider : IChatProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class FakeSettings : IAppSettingsStore
    {
        public string Location => "memory";

        public AppSettings Load() => AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        };

        public void Save(AppSettings settings)
        {
        }
    }
}
