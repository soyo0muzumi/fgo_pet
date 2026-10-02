using System.Net.Http;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;

namespace FgoPet.App.Providers
{

public sealed class ChatProviderFactory
{
    private readonly ProviderCatalog _catalog;
    private readonly ICredentialReader _credentialReader;
    private readonly HttpClient _httpClient;

    public ChatProviderFactory(
        ProviderCatalog catalog,
        ICredentialReader credentialReader,
        HttpClient httpClient)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public IChatProvider Create(ModelConnectionSettings settings) => Create(settings, apiKeyOverride: null);

    public IChatProvider Create(ModelConnectionSettings settings, string? apiKeyOverride)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "模型服务地址无效。");
        }

        _ = _catalog.Get(settings.ProviderId);
        var credentialReader = string.IsNullOrWhiteSpace(apiKeyOverride)
            ? _credentialReader
            : new InlineCredentialReader(apiKeyOverride.Trim());
        return new OpenAiCompatibleChatProvider(
            settings.ProviderId,
            baseUri,
            settings.ModelId,
            credentialReader,
            _httpClient);
    }

    private sealed class InlineCredentialReader(string secret) : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(secret);
        }
    }
}

}

namespace FgoPet.App.Dialogue
{
public sealed class ConfiguredChatProviderResolver : IChatProviderResolver
{
    private readonly IDialogueSettingsStore _settings;
    private readonly ChatProviderFactory _factory;

    public ConfiguredChatProviderResolver(IDialogueSettingsStore settings, ChatProviderFactory factory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IChatProvider Resolve()
    {
        var settings = _settings.Load().ModelConnection;
        return settings is null
            ? throw new ProviderRequestException(ProviderFailureCategory.Configuration, "尚未配置模型连接。")
            : _factory.Create(settings);
    }

    public IChatProvider Resolve(ModelConnectionSettings settings) => _factory.Create(settings);
}

}
