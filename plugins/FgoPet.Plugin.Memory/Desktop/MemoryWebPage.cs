using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Memory;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Owns the Web command adapter for the existing conversation-memory settings page.</summary>
/// <remarks>
/// Reuses the DI singleton <see cref="MemoryViewModel"/> so selection, edit drafts and lists survive
/// page navigation exactly like the WPF page; each Web session gets an independent adapter that shares
/// that same singleton view model (see <see cref="CreateSession"/>).
/// The active servant is deliberately not settable from the Web surface: the WPF page does not offer a
/// role picker either, the host context connector owns it. Operation outcomes are reported through the
/// view model's <c>StatusText</c>, matching the WPF page, which has no separate error channel.
/// </remarks>
public sealed class MemoryWebPage : ISettingsWebPage
{
    private static readonly string[] PageCommands =
    [
        "memory.get",
        "memory.setEnabled",
        "memory.refresh",
        "memory.selectCandidate",
        "memory.selectMemory",
        "memory.approveCandidate",
        "memory.rejectCandidate",
        "memory.editCandidate",
        "memory.enableMemory",
        "memory.disableMemory",
        "memory.editMemory",
        "memory.deleteMemory",
        "memory.selectReplacement",
        "memory.deleteAll",
    ];

    private readonly MemoryViewModel _viewModel;

    public MemoryWebPage(MemoryViewModel viewModel) =>
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    public string SettingsPageId => "ConversationMemory";

    public string ModulePath => "pages/conversation-memory.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new MemoryWebPage(_viewModel);

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPageId(message.Payload, out var pageId)
                || !string.Equals(pageId, SettingsPageId, StringComparison.Ordinal))
                return Failure("SETTINGS_INVALID_INPUT");

            return message.Type switch
            {
                "memory.get" => Get(message.Payload, cancellationToken),
                "memory.setEnabled" => SetEnabled(message.Payload, cancellationToken),
                "memory.refresh" => await InvokeAsync(message.Payload, _viewModel.RefreshCommand, cancellationToken)
                    .ConfigureAwait(true),
                "memory.selectCandidate" => SelectCandidate(message.Payload, cancellationToken),
                "memory.selectMemory" => SelectMemory(message.Payload, cancellationToken),
                "memory.approveCandidate" => await ReviewCandidateAsync(message.Payload, _viewModel.ApproveCandidateCommand,
                    requiresText: false, cancellationToken).ConfigureAwait(true),
                "memory.rejectCandidate" => await ReviewCandidateAsync(message.Payload, _viewModel.RejectCandidateCommand,
                    requiresText: false, cancellationToken).ConfigureAwait(true),
                "memory.editCandidate" => await ReviewCandidateAsync(message.Payload, _viewModel.EditCandidateCommand,
                    requiresText: true, cancellationToken).ConfigureAwait(true),
                "memory.enableMemory" => await ReviewMemoryAsync(message.Payload, _viewModel.EnableMemoryCommand,
                    requiresText: false, cancellationToken).ConfigureAwait(true),
                "memory.disableMemory" => await ReviewMemoryAsync(message.Payload, _viewModel.DisableMemoryCommand,
                    requiresText: false, cancellationToken).ConfigureAwait(true),
                "memory.deleteMemory" => await ReviewMemoryAsync(message.Payload, _viewModel.DeleteMemoryCommand,
                    requiresText: false, cancellationToken).ConfigureAwait(true),
                "memory.editMemory" => await ReviewMemoryAsync(message.Payload, _viewModel.EditMemoryCommand,
                    requiresText: true, cancellationToken).ConfigureAwait(true),
                "memory.selectReplacement" => await SelectReplacementAsync(message.Payload, cancellationToken)
                    .ConfigureAwait(true),
                "memory.deleteAll" => await InvokeAsync(message.Payload, _viewModel.DeleteAllCommand, cancellationToken)
                    .ConfigureAwait(true),
                _ => Failure("SETTINGS_UNKNOWN_COMMAND"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("SETTINGS_UNAVAILABLE");
        }
    }

    private WebSurfaceCommandResult Get(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SetEnabled(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "value")
            || !payload.TryGetProperty("value", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        // The view model persists this immediately (OnMemoryEnabledChanged), it is not a draft.
        _viewModel.MemoryEnabled = value.GetBoolean();
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SelectCandidate(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryReadId(payload, "candidateId", out var candidateId))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySelectCandidate(candidateId))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SelectMemory(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryReadId(payload, "memoryId", out var memoryId))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySelectMemory(memoryId))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> ReviewCandidateAsync(JsonElement payload,
        IAsyncRelayCommand command, bool requiresText, CancellationToken cancellationToken)
    {
        if (!TryReadId(payload, "candidateId", out var candidateId))
            return Failure("SETTINGS_INVALID_INPUT");

        // editCandidate carries the draft text in the same payload; other review commands take no text.
        if (requiresText)
        {
            if (!HasOnlyProperties(payload, "pageId", "candidateId", "text")
                || !payload.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || textElement.GetString() is not { } text)
                return Failure("SETTINGS_INVALID_INPUT");
        }
        else if (!HasOnlyProperties(payload, "pageId", "candidateId"))
        {
            return Failure("SETTINGS_INVALID_INPUT");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySelectCandidate(candidateId))
            return Failure("SETTINGS_INVALID_INPUT");

        if (requiresText)
        {
            _viewModel.CandidateEditText = payload.GetProperty("text").GetString() ?? string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await command.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> ReviewMemoryAsync(JsonElement payload,
        IAsyncRelayCommand command, bool requiresText, CancellationToken cancellationToken)
    {
        if (!TryReadId(payload, "memoryId", out var memoryId))
            return Failure("SETTINGS_INVALID_INPUT");

        if (requiresText)
        {
            if (!HasOnlyProperties(payload, "pageId", "memoryId", "text")
                || !payload.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || textElement.GetString() is not { } text)
                return Failure("SETTINGS_INVALID_INPUT");
        }
        else if (!HasOnlyProperties(payload, "pageId", "memoryId"))
        {
            return Failure("SETTINGS_INVALID_INPUT");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySelectMemory(memoryId))
            return Failure("SETTINGS_INVALID_INPUT");

        if (requiresText)
        {
            _viewModel.MemoryEditText = payload.GetProperty("text").GetString() ?? string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await command.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> SelectReplacementAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryReadId(payload, "candidateId", out var candidateId))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        // The command needs both a candidate and a memory selected; the memory side keeps the current selection.
        if (!TrySelectCandidate(candidateId) || _viewModel.SelectedMemory is null)
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.SelectReplacementCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> InvokeAsync(JsonElement payload, IAsyncRelayCommand command,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await command.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private bool TrySelectCandidate(string candidateId)
    {
        var match = _viewModel.Candidates.FirstOrDefault(
            candidate => string.Equals(candidate.CandidateId, candidateId, StringComparison.Ordinal));
        if (match is null) return false;
        _viewModel.SelectedCandidate = match;
        return true;
    }

    private bool TrySelectMemory(string memoryId)
    {
        var match = _viewModel.StoredMemories.FirstOrDefault(
            memory => string.Equals(memory.MemoryId, memoryId, StringComparison.Ordinal));
        if (match is null) return false;
        _viewModel.SelectedMemory = match;
        return true;
    }

    private object Snapshot() => new
    {
        memoryEnabled = _viewModel.MemoryEnabled,
        activeServantId = _viewModel.ActiveServantId,
        hasActiveServant = !string.IsNullOrWhiteSpace(_viewModel.ActiveServantId),
        candidates = _viewModel.Candidates.Select(candidate => new
        {
            id = candidate.CandidateId,
            text = candidate.Text,
            hasReplacement = candidate.ReplacesMemoryId is not null,
        }).ToArray(),
        selectedCandidateId = _viewModel.SelectedCandidate?.CandidateId ?? string.Empty,
        candidateEditText = _viewModel.CandidateEditText,
        candidateDetails = _viewModel.CandidateDetails,
        storedMemories = _viewModel.StoredMemories.Select(memory => new
        {
            id = memory.MemoryId,
            text = memory.Text,
            isEnabled = memory.IsEnabled,
            version = memory.Version,
        }).ToArray(),
        selectedMemoryId = _viewModel.SelectedMemory?.MemoryId ?? string.Empty,
        memoryEditText = _viewModel.MemoryEditText,
        memoryDetails = _viewModel.MemoryDetails,
        statusText = _viewModel.StatusText,
        candidatesStatusText = _viewModel.CandidatesStatusText,
        storedMemoriesStatusText = _viewModel.StoredMemoriesStatusText,
        isBusy = _viewModel.IsLoading,
        isLoading = _viewModel.IsLoading,
    };

    private static bool TryReadId(JsonElement payload, string propertyName, out string id)
    {
        id = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && (id = element.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryReadPageId(JsonElement payload, out string pageId)
    {
        pageId = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("pageId", out var element)
            && element.ValueKind == JsonValueKind.String
            && (pageId = element.GetString() ?? string.Empty).Length > 0;
    }

    private static bool HasOnlyProperties(JsonElement payload, params string[] allowedNames)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        return payload.EnumerateObject().All(property => allowed.Contains(property.Name))
            && allowedNames.All(name => payload.TryGetProperty(name, out _));
    }

    private static WebSurfaceCommandResult Success(object payload) => new(true, payload);

    private static WebSurfaceCommandResult Failure(string errorCode) => new(false, ErrorCode: errorCode);
}
