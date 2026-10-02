using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Owns the Web command adapter for the existing model connection settings.</summary>
/// <remarks>
/// Reuses the DI singleton <see cref="ModelConnectionViewModel"/> so the draft survives page
/// navigation exactly like the WPF page. Each Web session gets an independent adapter that shares
/// that same singleton view model (see <see cref="CreateSession"/>).
/// </remarks>
public sealed class ModelConnectionWebPage : ISettingsWebPage
{
    private static readonly string[] PageCommands =
    [
        "modelConnection.get",
        "modelConnection.setDraft",
        "modelConnection.setShowReasoning",
        "modelConnection.test",
        "modelConnection.refreshModels",
        "modelConnection.save",
        "modelConnection.clearKey",
    ];

    private readonly ModelConnectionViewModel _viewModel;
    private readonly object _initGate = new();
    private Task? _initTask;

    public ModelConnectionWebPage(ModelConnectionViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public string SettingsPageId => nameof(SettingsSection.ModelConnection);

    public string ModulePath => "pages/model-connection.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new ModelConnectionWebPage(_viewModel);

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
                "modelConnection.get" => await GetAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "modelConnection.setDraft" => SetDraft(message.Payload, cancellationToken),
                "modelConnection.setShowReasoning" => SetShowReasoning(message.Payload, cancellationToken),
                "modelConnection.test" => await TestAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "modelConnection.refreshModels" => await RefreshModelsAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "modelConnection.save" => await SaveAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "modelConnection.clearKey" => await ClearKeyAsync(message.Payload, cancellationToken).ConfigureAwait(true),
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

    private async ValueTask<WebSurfaceCommandResult> GetAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        await EnsureKeyStateAsync().ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SetDraft(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "field", "value")
            || !payload.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || fieldElement.GetString() is not { } field
            || !payload.TryGetProperty("value", out var valueElement))
            return Failure("SETTINGS_INVALID_INPUT");

        switch (field)
        {
            case "providerId" when TryGetString(valueElement, out var providerId)
                && _viewModel.Providers.Any(candidate =>
                    string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal)):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.SelectedProviderId = providerId;
                break;
            case "baseUrl" when TryGetString(valueElement, out var baseUrl):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.BaseUrl = baseUrl;
                break;
            case "modelId" when TryGetString(valueElement, out var modelId):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.ModelId = modelId;
                break;
            case "contextWindowOverride" when TryGetString(valueElement, out var overrideText):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.ContextWindowOverrideText = overrideText;
                break;
            case "maxOutputTokens" when TryGetString(valueElement, out var maxOutputTokens):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.MaxOutputTokensText = maxOutputTokens;
                break;
            case "apiKey" when TryGetString(valueElement, out var apiKey):
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.SetApiKey(apiKey);
                break;
            default:
                return Failure("SETTINGS_INVALID_INPUT");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SetShowReasoning(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "value")
            || !payload.TryGetProperty("value", out var valueElement)
            || valueElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        _viewModel.ShowReasoning = valueElement.GetBoolean();
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> TestAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.TestCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_TEST_FAILED");
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> RefreshModelsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.RefreshModelsCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_TEST_FAILED");
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> SaveAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_SAVE_FAILED");
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> ClearKeyAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.ClearKeyCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private object Snapshot() => new
    {
        providerId = _viewModel.SelectedProviderId,
        providers = _viewModel.Providers.Select(provider => new
        {
            id = provider.ProviderId,
            displayName = provider.DisplayName,
            defaultBaseUrl = provider.DefaultBaseUrl,
        }).ToArray(),
        baseUrl = _viewModel.BaseUrl,
        modelId = _viewModel.ModelId,
        availableModels = _viewModel.AvailableModels.Select(model => new
        {
            id = model.Id,
            displayName = model.DisplayName,
            contextWindowTokens = model.ContextWindowTokens,
            maxOutputTokens = model.MaxOutputTokens,
        }).ToArray(),
        contextWindowOverrideText = _viewModel.ContextWindowOverrideText,
        maxOutputTokensText = _viewModel.MaxOutputTokensText,
        contextLimitText = _viewModel.ContextLimitText,
        providerStatusText = _viewModel.ProviderStatusText,
        modelStatusText = _viewModel.ModelStatusText,
        keyStateText = _viewModel.KeyStateText,
        isKeySaved = _viewModel.IsKeySaved,
        showReasoning = _viewModel.ShowReasoning,
        isBusy = _viewModel.IsBusy,
        statusText = _viewModel.StatusText,
        errorText = _viewModel.ErrorText,
    };

    private Task EnsureKeyStateAsync()
    {
        lock (_initGate)
        {
            _initTask ??= _viewModel.InitializeAsync(CancellationToken.None);
            return _initTask;
        }
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
        {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
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
