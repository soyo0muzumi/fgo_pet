using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NewConversationRecallIsolationTests
{
    [Theory]
    [InlineData("今天好累呀")]
    [InlineData("你好")]
    public async Task Repeated_self_contained_message_in_new_conversation_does_not_resend_old_transcript(string text)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fgo-new-chat-{Guid.NewGuid():N}.db");
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var repository = new SqliteConversationRepository(database);
            var provider = new Provider();
            var resolver = new Resolver(provider);
            var settings = new Settings();
            var recall = new ConversationRecallService(new SqliteConversationRecallRepository(database), resolver, settings,
                new ModelContextResolver(_ => provider), new RequestTokenMeter());
            var context = new ContentContextKey("fixture-role", "fixture", "1", "default", "1", "1");
            var binding = new ContentBinding(context, new PersonaBundle("fixture-role", "fixture", "1", "1", "fixture persona", []),
                [], [], new string('a', 64), new string('b', 64));
            var orchestrator = new ConversationOrchestrator(resolver, new Content(binding), repository, new PromptComposer(),
                TimeProvider.System, settings: settings, recall: recall);

            var first = await orchestrator.SendAsync("fixture-role", text, default);
            Assert.Equal(ConversationSendStatus.Completed, first.Status);
            orchestrator.StartNewConversation("fixture-role");
            Assert.Null(repository.ReadState("LastActiveConversationId:fixture-role"));
            var second = await orchestrator.SendAsync("fixture-role", text, default);

            Assert.Equal(ConversationSendStatus.Completed, second.Status);
            Assert.NotEqual(first.ConversationId, second.ConversationId);
            Assert.Equal(2, repository.LoadMessages(second.ConversationId, "fixture-role").Count);
            Assert.DoesNotContain(provider.Request!.Messages, message => message.Text.Contains("OLD_CONTEXT_SENTINEL"));
            Assert.Contains(text, provider.Request.Messages[^1].Text);
            Assert.Equal(0, provider.AuxiliaryCalls);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) System.IO.File.Delete(path + suffix);
        }
    }

    private sealed class Resolver(Provider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }
    private sealed class Content(ContentBinding binding) : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(binding);
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with
        {
            ModelConnection = new("test", "https://fixture.test", "test", contextWindowOverride: 32768)
        };
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "test";
        public ChatRequest? Request { get; private set; }
        public int AuxiliaryCalls { get; private set; }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Metadata.ContainsKey("fgo_auxiliary")) AuxiliaryCalls++;
            Request = request;
            await Task.Yield();
            yield return new("""{"text":"OLD_CONTEXT_SENTINEL","emotion":"neutral"}""", IsComplete: true, FinishReason: "stop");
        }
    }
}
