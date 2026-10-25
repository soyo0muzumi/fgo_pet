using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Providers;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;

namespace FgoPet.App.Settings;

public sealed partial class ModelConnectionViewModel : ObservableObject
{
    private readonly IDialogueSettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly ProviderCatalog _catalog;
    private readonly ChatProviderFactory _providerFactory;
    private readonly TimeProvider _clock;
    private string _pendingApiKey = string.Empty;
    private long _draftVersion;
    private long _connectionVersion;
    private long _keyCheckVersion;
    private long _operationVersion;
    private DateTimeOffset _modelsRetryAfter;
    private ModelQuery? _modelQuery;

    public static readonly TimeSpan ModelsCacheLifetime = TimeSpan.FromMinutes(5);

    public ModelConnectionViewModel(
        IDialogueSettingsStore settings,
        ICredentialStore credentials,
        ProviderCatalog catalog,
        ChatProviderFactory providerFactory,
        TimeProvider? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _clock = clock ?? TimeProvider.System;
        ConfiguredModels = new(() => _settings.Load().ModelConnection,
            (connection, token) => _providerFactory.Create(connection).ListModelsAsync(token));
        Providers = _catalog.Providers;

        var saved = _settings.Load().ModelConnection;
        var selected = saved is null ? Providers[0] : _catalog.Get(saved.ProviderId);
        SelectedProviderId = selected.ProviderId;
        BaseUrl = saved?.BaseUrl ?? selected.DefaultBaseUrl;
        ModelId = saved?.ModelId ?? DefaultModel(selected.ProviderId);
        ContextWindowOverrideText = saved?.ContextWindowOverride?.ToString() ?? string.Empty;
        MaxOutputTokensText = (saved?.MaxOutputTokens ?? 2048).ToString();
        ShowReasoning = _settings.Load().ShowReasoning;
        AvailableModels = Array.Empty<ProviderModel>();
        StatusText = "未测试连接。";
        TestCommand = new AsyncRelayCommand(TestAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ClearKeyCommand = new AsyncRelayCommand(ClearKeyAsync);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync);
    }

    public IReadOnlyList<ProviderDescriptor> Providers { get; }

    /// <summary>Raised after the persisted connection becomes the active app configuration.</summary>
    public event Action<ModelConnectionSettings>? ConnectionSaved;
    public ConfiguredModelCatalog ConfiguredModels { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderStatusText))]
    private string _selectedProviderId;

    partial void OnSelectedProviderIdChanged(string value)
    {
        _pendingApiKey = string.Empty;
        IsKeySaved = false;
        InvalidateDraftConnection();
        var provider = Providers.FirstOrDefault(candidate => candidate.ProviderId == value);
        if (provider is null)
        {
            return;
        }

        BaseUrl = provider.DefaultBaseUrl;
        ModelId = DefaultModel(provider.ProviderId);
        AvailableModels = Array.Empty<ProviderModel>();
        IsModelPickerOpen = false;
    }

    [ObservableProperty]
    private string _baseUrl;

    partial void OnBaseUrlChanged(string value)
    {
        InvalidateDraftConnection();
        OnPropertyChanged(nameof(ContextLimitText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText))]
    private string _modelId;

    partial void OnModelIdChanged(string value)
    {
        _draftVersion++;
        OnPropertyChanged(nameof(ContextLimitText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextLimitText))]
    private IReadOnlyList<ProviderModel> _availableModels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextLimitText))]
    private string _contextWindowOverrideText = string.Empty;
    partial void OnContextWindowOverrideTextChanged(string value) => _draftVersion++;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextLimitText))]
    private string _maxOutputTokensText = "2048";
    partial void OnMaxOutputTokensTextChanged(string value) => _draftVersion++;

    public string ContextLimitText
    {
        get
        {
            try { return CurrentLimit(BuildConnection()).DisplayText + "；输入按完整请求保守估算。"; }
            catch (ArgumentException) { return "请输入有效的上下文上限和最大输出。"; }
        }
    }

    private ModelConnectionSettings BuildConnection()
    {
        int? capacity = null;
        if (!string.IsNullOrWhiteSpace(ContextWindowOverrideText))
        {
            if (!int.TryParse(ContextWindowOverrideText, out var parsed) || parsed <= 0)
                throw new ArgumentException("上下文上限须为正整数。");
            capacity = parsed;
        }
        if (!int.TryParse(MaxOutputTokensText, out var output) || output <= 0)
            throw new ArgumentException("最大输出须为正整数。");
        var previous = _settings.Load().ModelConnection;
        var supportsTools = previous is null || previous.ProviderId != SelectedProviderId ||
            previous.BaseUrl != BaseUrl || previous.ModelId != ModelId || previous.ToolsSupported;
        return new(SelectedProviderId, BaseUrl, ModelId, supportsTools, capacity, output);
    }

    private ModelContextLimit CurrentLimit(ModelConnectionSettings connection)
    {
        var route = ModelRouteKey.From(connection);
        if (connection.ContextWindowOverride is int capacity)
            return new(route, capacity, null, ContextLimitSource.Override, "manual");
        var metadata = AvailableModels?.FirstOrDefault(model => model.Id == connection.ModelId);
        if (metadata?.ContextWindowTokens is int window)
            return new(route, window, metadata.MaxOutputTokens, ContextLimitSource.ProviderMetadata, "provider-model-list");
        return KnownModelContextCatalog.Find(connection) ??
            ModelContextLimit.CreateFallback(route);
    }

    [ObservableProperty]
    private bool _isModelPickerOpen;

    [ObservableProperty]
    private bool _isKeySaved;

    partial void OnIsKeySavedChanged(bool value) => OnPropertyChanged(nameof(KeyStateText));

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>Dialogue window reasoning well toggle (spec §9.3); persists immediately.</summary>
    [ObservableProperty]
    private bool _showReasoning = true;

    partial void OnShowReasoningChanged(bool value)
    {
        // Serialize the read-modify-write of the shared DialogueSettings document so a concurrent
        // Save (ModelConnection) or another owner writing the same document cannot lose this update.
        lock (_settings)
        {
            _settings.Save(_settings.Load() with { ShowReasoning = value });
        }
        ShowReasoningChanged?.Invoke(value);
    }

    /// <summary>Raised so the live conversation view applies the toggle without a round-trip.</summary>
    public event Action<bool>? ShowReasoningChanged;

    public IAsyncRelayCommand TestCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand ClearKeyCommand { get; }

    public IAsyncRelayCommand RefreshModelsCommand { get; }

    public string ProviderStatusText =>
        Providers.FirstOrDefault(provider => provider.ProviderId == SelectedProviderId)?.DisplayName ?? SelectedProviderId;

    public string ModelStatusText => string.IsNullOrWhiteSpace(ModelId) ? "未选择模型" : ModelId;

    public string KeyStateText => IsKeySaved ? "已保存密钥（存储在 Windows Credential Manager）" : "尚未保存密钥。";

    public void SetApiKey(string value)
    {
        var pending = value?.Trim() ?? string.Empty;
        if (string.Equals(_pendingApiKey, pending, StringComparison.Ordinal)) return;
        _pendingApiKey = pending;
        InvalidateDraftConnection();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var providerId = SelectedProviderId;
        var connectionVersion = _connectionVersion;
        var checkVersion = ++_keyCheckVersion;
        var exists = await _credentials.ExistsAsync(CredentialTarget(providerId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (connectionVersion == _connectionVersion && checkVersion == _keyCheckVersion
            && providerId == SelectedProviderId)
            IsKeySaved = exists;
    }

    private Task TestAsync() => QueryModelsAsync(explicitRequest: true, test: true);

    private Task RefreshModelsAsync() => QueryModelsAsync(explicitRequest: true);

    /// <summary>Loads models for this draft on demand without saving or selecting anything.</summary>
    public Task EnsureModelsAsync(CancellationToken cancellationToken = default) =>
        QueryModelsAsync(explicitRequest: false, cancellationToken: cancellationToken);

    private Task QueryModelsAsync(bool explicitRequest, bool test = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_modelQuery is { } current && current.ConnectionVersion == _connectionVersion
            && current.DraftVersion == _draftVersion)
        {
            current.ExplicitRequest |= explicitRequest;
            current.Test |= test;
            return WaitForQuery(current.Completion.Task, cancellationToken);
        }
        if (!explicitRequest && _clock.GetUtcNow() < _modelsRetryAfter) return Task.CompletedTask;
        // A passive page refresh must not supersede an explicit credential save/delete.
        if (!explicitRequest && IsBusy && _modelQuery is null) return Task.CompletedTask;

        var operationVersion = BeginOperation();
        var query = new ModelQuery(_connectionVersion, _draftVersion, operationVersion,
            SelectedProviderId, BaseUrl, ModelId, _pendingApiKey, explicitRequest, test,
            new CancellationTokenSource(TimeSpan.FromSeconds(15), _clock));
        _modelQuery = query;
        _ = RunModelQueryAsync(query);
        // Closing one page cancels its wait, not the shared request used by another page.
        return WaitForQuery(query.Completion.Task, cancellationToken);
    }

    private static Task WaitForQuery(Task task, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;

    private bool OwnsQuery(ModelQuery query) => ReferenceEquals(_modelQuery, query)
        && query.ConnectionVersion == _connectionVersion && query.OperationVersion == _operationVersion;

    private async Task RunModelQueryAsync(ModelQuery query)
    {
        try
        {
            var token = query.Cancellation.Token;
            if (string.IsNullOrEmpty(query.PendingKey))
            {
                var exists = await _credentials.ExistsAsync(CredentialTarget(query.ProviderId), token).WaitAsync(token);
                if (!OwnsQuery(query)) return;
                IsKeySaved = exists;
                if (!exists)
                {
                    if (!query.ExplicitRequest) return;
                    throw new ProviderRequestException(ProviderFailureCategory.Configuration, "尚未配置模型 API Key。");
                }
            }

            // Listing models does not require a selected model. This fallback is request metadata only.
            var connection = new ModelConnectionSettings(query.ProviderId, query.BaseUrl,
                string.IsNullOrWhiteSpace(query.ModelId) ? DefaultModel(query.ProviderId) : query.ModelId);
            var models = await _providerFactory.Create(connection, query.PendingKey)
                .ListModelsAsync(token).WaitAsync(token);
            if (!OwnsQuery(query)) return;
            if (query.DraftVersion != _draftVersion)
            {
                StatusText = query.Test ? "测试完成，但草稿已更改；结果未应用。" : "刷新完成，但草稿已更改；结果未应用。";
                return;
            }
            AvailableModels = models;
            _modelsRetryAfter = _clock.GetUtcNow() + ModelsCacheLifetime;
            StatusText = query.Test ? $"连接成功（尚未保存） · {ProviderStatusText} · {ModelStatusText}"
                : $"已刷新 {AvailableModels.Count} 个模型。";
        }
        catch (Exception error) when (IsProviderOperationFailure(error))
        {
            if (!OwnsQuery(query) || query.DraftVersion != _draftVersion) return;
            _modelsRetryAfter = _clock.GetUtcNow() + TimeSpan.FromSeconds(30);
            ErrorText = ProviderOperationError(query.Test ? "测试连接" : "刷新模型", error);
        }
        catch (Exception error)
        {
            if (OwnsQuery(query) && query.DraftVersion == _draftVersion)
                query.Completion.TrySetException(error);
        }
        finally
        {
            if (ReferenceEquals(_modelQuery, query)) _modelQuery = null;
            EndOperation(query.OperationVersion);
            query.Cancellation.Dispose();
            query.Completion.TrySetResult();
        }
    }

    private async Task SaveAsync()
    {
        var operationVersion = BeginOperation();
        var connectionVersion = _connectionVersion;
        var draftVersion = _draftVersion;
        bool OwnsSave() => operationVersion == _operationVersion && connectionVersion == _connectionVersion
            && draftVersion == _draftVersion;
        try
        {
            var connection = BuildConnection();
            _ = PromptBudget.Resolve(CurrentLimit(connection), connection.MaxOutputTokens);
            var pendingKey = _pendingApiKey;
            if (!string.IsNullOrEmpty(pendingKey))
            {
                await _credentials.SaveAsync(CredentialTarget(connection.ProviderId), pendingKey, CancellationToken.None);
                // The captured credential may have changed even if the user has since moved to another draft.
                ConfiguredModels.Invalidate();
                if (!OwnsSave()) return;
                _keyCheckVersion++;
                IsKeySaved = true;
                _pendingApiKey = string.Empty;
                _draftVersion++;
                _connectionVersion++;
                draftVersion = _draftVersion;
                connectionVersion = _connectionVersion;
                _modelsRetryAfter = default;
            }
            else
            {
                var exists = await _credentials.ExistsAsync(CredentialTarget(connection.ProviderId), CancellationToken.None);
                if (!OwnsSave()) return;
                _keyCheckVersion++;
                IsKeySaved = exists;
                if (!exists)
                    throw new ProviderRequestException(ProviderFailureCategory.Configuration, "请先输入 API Key。");
            }

            lock (_settings)
            {
                _settings.Save(_settings.Load() with { ModelConnection = connection });
            }
            ConnectionSaved?.Invoke(connection);
            StatusText = $"已保存 · {ProviderStatusText} · {ModelStatusText}";
        }
        catch (ArgumentException error)
        {
            if (OwnsSave()) ErrorText = $"设置无效：{error.Message}";
        }
        catch (PromptBudgetException)
        {
            if (OwnsSave()) ErrorText = "最大输出超出模型限制，或没有为输入留下空间。";
        }
        catch (ProviderRequestException error)
        {
            if (OwnsSave()) ErrorText = error.Message;
        }
        finally
        {
            EndOperation(operationVersion);
        }
    }

    private async Task ClearKeyAsync()
    {
        var operationVersion = BeginOperation();
        var connectionVersion = _connectionVersion;
        var providerId = SelectedProviderId;
        try
        {
            await _credentials.DeleteAsync(CredentialTarget(providerId), CancellationToken.None);
            ConfiguredModels.Invalidate();
            if (operationVersion != _operationVersion || connectionVersion != _connectionVersion) return;
            _pendingApiKey = string.Empty;
            IsKeySaved = false;
            InvalidateDraftConnection();
            StatusText = "API Key 已清除。";
        }
        finally { EndOperation(operationVersion); }
    }

    private long BeginOperation()
    {
        var version = ++_operationVersion;
        CancelModelQuery();
        IsBusy = true;
        ErrorText = string.Empty;
        return version;
    }

    private void EndOperation(long version)
    {
        if (version == _operationVersion) IsBusy = false;
    }

    private void InvalidateDraftConnection()
    {
        _draftVersion++;
        _connectionVersion++;
        _keyCheckVersion++;
        _operationVersion++;
        CancelModelQuery();
        _modelsRetryAfter = default;
        AvailableModels = Array.Empty<ProviderModel>();
        IsBusy = false;
        ErrorText = string.Empty;
        StatusText = "未测试连接。";
    }

    private void CancelModelQuery()
    {
        var query = _modelQuery;
        _modelQuery = null;
        try { query?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* Completion raced with detaching the old query. */ }
    }

    private static bool IsProviderOperationFailure(Exception error) => error is ArgumentException
        or ProviderRequestException or OperationCanceledException or System.Net.Http.HttpRequestException
        or System.IO.IOException or System.ComponentModel.Win32Exception;

    private static string ProviderOperationError(string operation, Exception error) => error switch
    {
        ArgumentException => $"{operation}失败：设置无效。",
        ProviderRequestException providerError => $"{operation}失败：{providerError.Message}",
        OperationCanceledException => $"{operation}失败：请求超时，请重试。",
        System.ComponentModel.Win32Exception => $"{operation}失败：无法读取已保存的凭据。",
        _ => $"{operation}失败：无法连接模型服务。",
    };

    private sealed class ModelQuery(long connectionVersion, long draftVersion, long operationVersion,
        string providerId, string baseUrl, string modelId, string pendingKey, bool explicitRequest,
        bool test, CancellationTokenSource cancellation)
    {
        public long ConnectionVersion { get; } = connectionVersion;
        public long DraftVersion { get; } = draftVersion;
        public long OperationVersion { get; } = operationVersion;
        public string ProviderId { get; } = providerId;
        public string BaseUrl { get; } = baseUrl;
        public string ModelId { get; } = modelId;
        public string PendingKey { get; } = pendingKey;
        public bool ExplicitRequest { get; set; } = explicitRequest;
        public bool Test { get; set; } = test;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string CredentialTarget(string providerId) => $"fgo-pet/provider/{providerId}";

    private static string DefaultModel(string providerId) =>
        providerId switch
        {
            "deepseek" => "deepseek-chat",
            _ => "gpt-4o-mini",
        };
}
