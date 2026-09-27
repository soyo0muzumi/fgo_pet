using FgoPet.Infrastructure.Dialogue;
using FgoPet.Character.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Privacy;

/// <summary>Deletes user dialogue data while keeping explicit memory ownership clear.</summary>
public sealed class UserDataDeletionService : IUserDataDeleter
{
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteMemoryRepository _memories;
    private readonly ICredentialStore? _credentials;
    private readonly IDialogueSettingsStore? _dialogueSettings;
    private readonly ICharacterSettingsStore? _characterSettings;
    private readonly ProviderCatalog? _catalog;
    private readonly IDialogueContextLifetime? _dialogueLifetime;

    public UserDataDeletionService(
        RuntimeDatabase database,
        SqliteConversationRepository conversations,
        SqliteMemoryRepository memories,
        ICredentialStore? credentials = null,
        IDialogueSettingsStore? dialogueSettings = null,
        ICharacterSettingsStore? characterSettings = null,
        ProviderCatalog? catalog = null,
        IDialogueContextLifetime? dialogueLifetime = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _credentials = credentials;
        _dialogueSettings = dialogueSettings;
        _characterSettings = characterSettings;
        _catalog = catalog;
        _dialogueLifetime = dialogueLifetime;
    }

    public async Task DeleteConversationAsync(string conversationId, string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _memories.InvalidateWrites();
        using var suspension = _dialogueLifetime is null ? null : await _dialogueLifetime.SuspendAsync(cancellationToken);
        _conversations.DeleteConversation(conversationId, servantId);
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
    /// model metadata, user profile, package preferences, and servant address
    /// preferences. Phase 2 focus/bond history remains outside this control.
    /// </summary>
    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _memories.InvalidateWrites();
        using var suspension = _dialogueLifetime is null ? null : await _dialogueLifetime.SuspendAsync(cancellationToken);
        var dialogue = _dialogueSettings?.Load();
        if (_credentials is not null)
        {
            var providerIds = _catalog?.Providers.Select(provider => provider.ProviderId)
                ?? (dialogue?.ModelConnection is { } model
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
            DELETE FROM runtime_state;
            DELETE FROM memory_ingestions;
            UPDATE memory_write_state SET generation=lower(hex(randomblob(16))),revision=revision+1 WHERE singleton_id=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        if (_dialogueSettings is not null)
        {
            _dialogueSettings.Save(_dialogueSettings.Load() with { ModelConnection = null });
        }

        if (_characterSettings is not null)
        {
            _characterSettings.Save(_characterSettings.Load() with
            {
                ServantPreferences = new Dictionary<string, ServantPreference>(StringComparer.Ordinal),
                UserProfile = null,
                PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            });
        }
        return;
    }
}
