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
    private string _pendingApiKey = string.Empty;
    private long _draftVersion;

    public ModelConnectionViewModel(
        IDialogueSettingsStore settings,
        ICredentialStore credentials,
        ProviderCatalog catalog,
        ChatProviderFactory providerFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderStatusText))]
    private string _selectedProviderId;

    partial void OnSelectedProviderIdChanged(string value)
    {
        _draftVersion++;
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
        _draftVersion++;
        AvailableModels = Array.Empty<ProviderModel>();
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
            new ModelContextLimit(route, 8192, null, ContextLimitSource.ConservativeFallback, "fallback-v1");
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
        _settings.Save(_settings.Load() with { ShowReasoning = value });
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
        _pendingApiKey = value?.Trim() ?? string.Empty;
        _draftVersion++;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsKeySaved = await _credentials.ExistsAsync(CredentialTarget(), cancellationToken);
    }

    private async Task TestAsync()
    {
        var testedDraftVersion = _draftVersion;
        await ExecuteProviderOperationAsync("测试连接", async provider =>
        {
            var models = await provider.ListModelsAsync(CancellationToken.None);
            if (testedDraftVersion != _draftVersion)
            {
                StatusText = "测试完成，但草稿已更改；结果未应用。";
                return;
            }

            AvailableModels = models;
            StatusText = $"连接成功（尚未保存） · {ProviderStatusText} · {ModelStatusText}";
        });
    }

    private async Task RefreshModelsAsync()
    {
        var testedDraftVersion = _draftVersion;
        await ExecuteProviderOperationAsync("刷新模型", async provider =>
        {
            var models = await provider.ListModelsAsync(CancellationToken.None);
            if (testedDraftVersion != _draftVersion)
            {
                StatusText = "刷新完成，但草稿已更改；结果未应用。";
                return;
            }
            AvailableModels = models;
            StatusText = $"已刷新 {AvailableModels.Count} 个模型。";
        });
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var connection = BuildConnection();
            _ = PromptBudget.Resolve(CurrentLimit(connection), connection.MaxOutputTokens);
            if (!string.IsNullOrEmpty(_pendingApiKey))
            {
                await _credentials.SaveAsync(CredentialTarget(), _pendingApiKey, CancellationToken.None);
                IsKeySaved = true;
                _pendingApiKey = string.Empty;
                _draftVersion++;
            }
            else if (!IsKeySaved)
            {
                throw new ProviderRequestException(ProviderFailureCategory.Configuration, "请先输入 API Key。");
            }

            _settings.Save(_settings.Load() with { ModelConnection = connection });
            ConnectionSaved?.Invoke(connection);
            StatusText = $"已保存 · {ProviderStatusText} · {ModelStatusText}";
        }
        catch (ArgumentException error)
        {
            ErrorText = $"设置无效：{error.Message}";
        }
        catch (PromptBudgetException)
        {
            ErrorText = "最大输出超出模型限制，或没有为输入留下空间。";
        }
        catch (ProviderRequestException error)
        {
            ErrorText = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearKeyAsync()
    {
        await _credentials.DeleteAsync(CredentialTarget(), CancellationToken.None);
        _pendingApiKey = string.Empty;
        IsKeySaved = false;
        StatusText = "API Key 已清除。";
    }

    private async Task ExecuteProviderOperationAsync(string operation, Func<IChatProvider, Task> action)
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var connection = new ModelConnectionSettings(SelectedProviderId, BaseUrl, ModelId);
            await action(_providerFactory.Create(connection, _pendingApiKey));
        }
        catch (ArgumentException)
        {
            ErrorText = $"{operation}失败：设置无效。";
        }
        catch (ProviderRequestException error)
        {
            ErrorText = $"{operation}失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string CredentialTarget() => $"fgo-pet/provider/{SelectedProviderId}";

    private static string DefaultModel(string providerId) =>
        providerId switch
        {
            "deepseek" => "deepseek-chat",
            _ => "gpt-4o-mini",
        };
}
