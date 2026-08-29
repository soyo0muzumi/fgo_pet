using FgoPet.App.Providers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class ModelConnectionViewModelTests
{
    [Fact]
    public async Task Save_persists_provider_and_model_metadata_but_sends_key_to_credential_store()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials);
        viewModel.SelectedProviderId = "deepseek";
        viewModel.BaseUrl = "https://api.deepseek.com/v1";
        viewModel.ModelId = "deepseek-chat";
        viewModel.SetApiKey("secret-value");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("deepseek", settings.Saved!.ModelConnection!.ProviderId);
        Assert.Equal("deepseek-chat", settings.Saved.ModelConnection.ModelId);
        Assert.Equal("secret-value", credentials.Values["fgo-pet/provider/deepseek"]);
        Assert.True(viewModel.IsKeySaved);
        Assert.DoesNotContain("secret-value", System.Text.Json.JsonSerializer.Serialize(settings.Saved));
    }

    [Fact]
    public void Provider_catalog_exposes_provider_and_model_labels_for_status()
    {
        var viewModel = CreateViewModel(new FakeSettings(), new FakeCredentials());

        Assert.Contains(viewModel.Providers, provider => provider.ProviderId == "openai" && provider.DisplayName == "OpenAI");
        Assert.Contains(viewModel.Providers, provider => provider.ProviderId == "deepseek" && provider.DisplayName == "DeepSeek");
        Assert.Equal("openai", viewModel.SelectedProviderId);
        Assert.Equal("gpt-4o-mini", viewModel.ModelId);
    }

    [Fact]
    public async Task Test_connection_uses_newly_entered_key_before_save()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var handler = new RespondingHandler();
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(catalog, credentials, new HttpClient(handler));
        var viewModel = new ModelConnectionViewModel(settings, credentials, catalog, factory);
        viewModel.SetApiKey("new-key");

        await viewModel.TestCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ErrorText);
        Assert.Equal("Bearer new-key", handler.AuthorizationHeader);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task Save_with_invalid_metadata_reports_error_without_throwing()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials);
        viewModel.BaseUrl = string.Empty;
        viewModel.SetApiKey("secret-value");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(viewModel.ErrorText);
        Assert.Null(settings.Saved);
    }

    private static ModelConnectionViewModel CreateViewModel(FakeSettings settings, FakeCredentials credentials)
    {
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(
            catalog,
            credentials,
            new HttpClient());
        return new ModelConnectionViewModel(settings, credentials, catalog, factory);
    }

    private sealed class FakeSettings : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Current { get; private set; } = AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        };
        public AppSettings? Saved { get; private set; }
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) { Current = settings; Saved = settings; }
    }

    private sealed class FakeCredentials : ICredentialStore, ICredentialReader
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[target] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.ContainsKey(target));
        }

        public Task DeleteAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(target);
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.TryGetValue(target, out var secret) ? secret : null);
        }
    }

    private sealed class RespondingHandler : HttpMessageHandler
    {
        public string? AuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new[] { new { id = "gpt-4o-mini" } } }),
            });
        }
    }
}
