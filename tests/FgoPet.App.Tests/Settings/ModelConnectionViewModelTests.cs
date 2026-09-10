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
    public async Task Save_raises_connection_saved_after_persisting_metadata()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials);
        viewModel.SelectedProviderId = "deepseek";
        viewModel.BaseUrl = "https://api.deepseek.com/v1";
        viewModel.ModelId = "deepseek-reasoner";
        ModelConnectionSettings? saved = null;
        viewModel.ConnectionSaved += connection => saved = connection;
        viewModel.SetApiKey("secret-value");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(settings.Saved!.ModelConnection, saved);
        Assert.Equal("deepseek", saved?.ProviderId);
        Assert.Equal("deepseek-reasoner", saved?.ModelId);
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
    public void Selecting_provider_updates_its_default_endpoint_and_model()
    {
        var viewModel = CreateViewModel(new FakeSettings(), new FakeCredentials());

        viewModel.SelectedProviderId = "deepseek";

        Assert.Equal("https://api.deepseek.com/v1", viewModel.BaseUrl);
        Assert.Equal("deepseek-chat", viewModel.ModelId);
        Assert.Equal("DeepSeek", viewModel.ProviderStatusText);
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
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/openai"));
        Assert.Null(settings.Saved);
    }

    [Fact]
    public async Task Successful_test_does_not_persist_or_activate_until_explicit_save()
    {
        var settings = new FakeSettings { Current = AppSettings.Defaults with { ModelConnection = null } };
        var credentials = new FakeCredentials();
        var handler = new RespondingHandler();
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(catalog, credentials, new HttpClient(handler));
        var viewModel = new ModelConnectionViewModel(settings, credentials, catalog, factory)
        {
            SelectedProviderId = "deepseek",
            BaseUrl = "https://api.deepseek.com/v1",
            ModelId = "deepseek-chat",
        };
        var savedCount = 0;
        viewModel.ConnectionSaved += _ => savedCount++;
        viewModel.SetApiKey("new-key");

        await viewModel.TestCommand.ExecuteAsync(null);

        Assert.Equal(0, savedCount);
        Assert.Null(settings.Saved);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));
        Assert.Contains("尚未保存", viewModel.StatusText, StringComparison.Ordinal);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, savedCount);
        Assert.Equal("deepseek", settings.Saved!.ModelConnection!.ProviderId);
        Assert.Equal("deepseek-chat", settings.Saved.ModelConnection.ModelId);
        Assert.Equal("new-key", credentials.Values["fgo-pet/provider/deepseek"]);
    }
    [Fact]
    public async Task Stale_test_result_does_not_overwrite_a_changed_draft()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var handler = new DelayedRespondingHandler();
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(catalog, credentials, new HttpClient(handler));
        var viewModel = new ModelConnectionViewModel(settings, credentials, catalog, factory);
        viewModel.SetApiKey("new-key");

        var test = viewModel.TestCommand.ExecuteAsync(null);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ModelId = "newer-model";
        handler.Complete();
        await test;

        Assert.Equal("newer-model", viewModel.ModelId);
        Assert.Empty(viewModel.AvailableModels);
        Assert.Contains("草稿已更改", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Null(settings.Saved);
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
        public AppSettings Current { get; set; } = AppSettings.Defaults with
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

    private sealed class DelayedRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        public void Complete() => _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { id = "stale-model" } } }),
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            cancellationToken.Register(() => _response.TrySetCanceled(cancellationToken));
            return _response.Task;
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
