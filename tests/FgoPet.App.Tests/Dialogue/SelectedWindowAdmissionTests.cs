using System.IO;
using System.Runtime.CompilerServices;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Platform.WindowText;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class SelectedWindowAdmissionTests
{
    [Fact]
    public async Task Admitted_desktop_send_consumes_selection_even_when_provider_fails_in_existing_conversation()
    {
        using var fixture = new Fixture();
        await fixture.Attach();
        fixture.Conversation.InputText = "analyze chosen window";
        await fixture.Conversation.SendCommand.ExecuteAsync(null);
        Assert.NotEmpty(fixture.Conversation.ErrorText);
        Assert.Contains(Assert.Single(fixture.Provider.Requests).Messages,
            message => message.Text.Contains("synthetic-private-window-text"));
        Assert.Null(fixture.Selection.CurrentContent);

        fixture.Conversation.InputText = "next independent message";
        await fixture.Conversation.SendCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fixture.Provider.Requests[^1].Messages,
            message => message.Text.Contains("synthetic-private-window-text"));
    }

    [Fact]
    public async Task External_send_never_inherits_or_consumes_desktop_window_selection()
    {
        using var fixture = new Fixture();
        await fixture.Attach();
        var result = await fixture.Conversation.SendExternalAsync(new("role", "existing", null), "external request", default);
        Assert.Equal(ExternalConversationStatus.Failed, result.Status);
        Assert.DoesNotContain(Assert.Single(fixture.Provider.Requests).Messages,
            message => message.Text.Contains("synthetic-private-window-text"));
        Assert.NotNull(fixture.Selection.CurrentContent);
    }

    [Fact]
    public async Task Empty_desktop_draft_does_not_consume_selection()
    {
        using var fixture = new Fixture();
        await fixture.Attach();
        fixture.Conversation.InputText = " ";
        await fixture.Conversation.SendCommand.ExecuteAsync(null);
        Assert.NotNull(fixture.Selection.CurrentContent);
        Assert.Empty(fixture.Provider.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"fgopet-window-admission-{Guid.NewGuid():N}.db");
        public Provider Provider { get; } = new();
        public ConversationViewModel Conversation { get; }
        public ChatWindowSelection Selection { get; }
        public Fixture()
        {
            var database = new RuntimeDatabase(path, pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var repository = new SqliteConversationRepository(database);
            var settings = new Settings();
            var context = new ContentContextKey("role", "fixture", "1", "default", "1", "1");
            repository.CreateConversation("existing", "role", context, DateTimeOffset.UtcNow);
            var orchestrator = new ConversationOrchestrator(new Resolver(Provider), new Content(context), repository,
                new PromptComposer(), TimeProvider.System, settings);
            Conversation = new(orchestrator, settings);
            Conversation.SetActiveServant("role");
            Conversation.SetActiveConversation("existing");
            Selection = new(new Reader(), Conversation.WindowContextScopeKey, () => Conversation.CanChooseWindowContext);
            Conversation.BindWindowSelection(Selection);
        }
        public async Task Attach()
        {
            await Selection.PreviewAsync("native-fixture-token", default);
            Assert.True(Selection.AttachPreview());
        }
        public void Dispose()
        {
            Selection.Dispose(); Conversation.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
    private sealed class Reader : IWindowTextReader
    {
        public IReadOnlyList<WindowTextCandidate> ListCandidates() => [];
        public Task<WindowTextSnapshot> ReadAsync(string candidateToken, CancellationToken cancellationToken) =>
            Task.FromResult(new WindowTextSnapshot("synthetic title", "fixture", "synthetic-private-window-text",
                DateTimeOffset.UtcNow, WindowTextReadStatus.Complete));
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with
        { ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model", false, 32768) };
        public void Save(DialogueSettings settings) { }
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Content(ContentContextKey context) : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(context, null, [], [], "", ""));
    }
    private sealed class Provider : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];
        public string ProviderId => "test";
        public string ModelId => "test-model";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
                throw new ProviderRequestException(ProviderFailureCategory.Network, "synthetic network failure", requestWasSent: true);
            yield break;
        }
    }
}
