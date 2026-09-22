using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.App.Tests.Dialogue;

/// <summary>In-memory settings store for dialogue presentation tests.</summary>
public sealed class TestDialogueSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
{
    public DialogueSettings Current { get; private set; } = initial;

    public DialogueSettings Load() => Current;

    public void Save(DialogueSettings settings) => Current = settings;

    public static TestDialogueSettingsStore WithModelConnection() => new(DialogueSettings.Defaults with
    {
        ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
    });
}

internal static class TestDialogueFakes
{
    public static ConversationViewModel CreateConversation(TestDialogueSettingsStore? suppliedSettings = null)
    {
        var settings = suppliedSettings ?? TestDialogueSettingsStore.WithModelConnection();
        var database = new FgoPet.Infrastructure.Persistence.RuntimeDatabase(
            Path.Combine(Path.GetTempPath(), $"fgo-p1b-{Guid.NewGuid():N}.db"));
        new FgoPet.Infrastructure.Persistence.RuntimeDatabaseMigrator(database).Migrate();
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(database),
            new FgoPet.Infrastructure.Memory.SqliteMemoryRepository(database),
            new PromptComposer(),
            TimeProvider.System,
            settings);
        return new ConversationViewModel(orchestrator, settings);
    }

    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() => throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
            FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null, Array.Empty<KnowledgeEntry>(), Array.Empty<string>(), string.Empty, string.Empty));
    }
}
