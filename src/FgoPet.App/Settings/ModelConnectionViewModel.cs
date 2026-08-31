using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;

namespace FgoPet.App.Settings;

public sealed partial class ModelConnectionViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly ProviderCatalog _catalog;
    private readonly ChatProviderFactory _providerFactory;
    private string _pendingApiKey = string.Empty;

    public ModelConnectionViewModel(
        IAppSettingsStore settings,
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
        var provider = Providers.FirstOrDefault(candidate => candidate.ProviderId == value);
        if (provider is null)
        {
            return;
        }

        BaseUrl = provider.DefaultBaseUrl;
        ModelId = DefaultModel(provider.ProviderId);
        AvailableModels = Array.Empty<ProviderModel>();
    }

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText))]
    private string _modelId;

    [ObservableProperty]
    private IReadOnlyList<ProviderModel> _availableModels;

    [ObservableProperty]
    private bool _isKeySaved;

    partial void OnIsKeySavedChanged(bool value) => OnPropertyChanged(nameof(KeyStateText));

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public IAsyncRelayCommand TestCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand ClearKeyCommand { get; }

    public IAsyncRelayCommand RefreshModelsCommand { get; }

    public string ProviderStatusText =>
        Providers.FirstOrDefault(provider => provider.ProviderId == SelectedProviderId)?.DisplayName ?? SelectedProviderId;

    public string ModelStatusText => string.IsNullOrWhiteSpace(ModelId) ? "未选择模型" : ModelId;

    public string KeyStateText => IsKeySaved ? "已保存密钥（存储在 Windows Credential Manager）" : "尚未保存密钥。";

    public void SetApiKey(string value) => _pendingApiKey = value?.Trim() ?? string.Empty;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsKeySaved = await _credentials.ExistsAsync(CredentialTarget(), cancellationToken);
    }

    private async Task TestAsync()
    {
        await ExecuteProviderOperationAsync("测试连接", async provider =>
        {
            var models = await provider.ListModelsAsync(CancellationToken.None);
            AvailableModels = models;
            StatusText = $"连接成功 · {ProviderStatusText} · {ModelStatusText}";
        });
    }

    private async Task RefreshModelsAsync()
    {
        await ExecuteProviderOperationAsync("刷新模型", async provider =>
        {
            AvailableModels = await provider.ListModelsAsync(CancellationToken.None);
            StatusText = $"已刷新 {AvailableModels.Count} 个模型。";
        });
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var connection = new ModelConnectionSettings(SelectedProviderId, BaseUrl, ModelId);
            if (!string.IsNullOrEmpty(_pendingApiKey))
            {
                await _credentials.SaveAsync(CredentialTarget(), _pendingApiKey, CancellationToken.None);
                IsKeySaved = true;
                _pendingApiKey = string.Empty;
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
