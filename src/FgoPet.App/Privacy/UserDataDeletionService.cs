using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Privacy;

/// <summary>Deletes user dialogue data while keeping explicit memory ownership clear.</summary>
public sealed class UserDataDeletionService
{
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteMemoryRepository _memories;
    private readonly ICredentialStore? _credentials;
    private readonly IAppSettingsStore? _settings;
    private readonly ProviderCatalog? _catalog;

    public UserDataDeletionService(
        RuntimeDatabase database,
        SqliteConversationRepository conversations,
        SqliteMemoryRepository memories,
        ICredentialStore? credentials = null,
        IAppSettingsStore? settings = null,
        ProviderCatalog? catalog = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _credentials = credentials;
        _settings = settings;
        _catalog = catalog;
    }

    public Task DeleteConversationAsync(string conversationId, string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _conversations.DeleteConversation(conversationId, servantId);
        return Task.CompletedTask;
    }

    public Task DeleteMemoryAsync(string memoryId, string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _memories.ReviewMemory(memoryId, servantId, FgoPet.Core.Memory.MemoryReviewAction.Delete, null, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes all Phase 3 conversation, summary, candidate, approved-memory,
    /// and content-binding records. It also clears the current model credential,
    /// model metadata, and servant address preferences. Phase 2 focus/bond history
    /// remains outside this control.
    /// </summary>
    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings?.Load();
        if (_credentials is not null)
        {
            var providerIds = _catalog?.Providers.Select(provider => provider.ProviderId)
                ?? (settings?.ModelConnection is { } model
                    ? [model.ProviderId]
                    : Array.Empty<string>());
            foreach (var providerId in providerIds.Distinct(StringComparer.Ordinal))
            {
                await _credentials.DeleteAsync($"fgo-pet/provider/{providerId}", cancellationToken);
            }
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM memories;
            DELETE FROM conversations;
            DELETE FROM content_bindings;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        if (settings is not null && _settings is not null)
        {
            _settings.Save(settings with
            {
                ModelConnection = null,
                ServantPreferences = new Dictionary<string, ServantPreference>(StringComparer.Ordinal),
                UserProfile = null,
                PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            });
        }
        return;
    }
}
