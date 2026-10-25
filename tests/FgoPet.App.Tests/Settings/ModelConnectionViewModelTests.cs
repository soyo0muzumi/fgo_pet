using FgoPet.App.Providers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class ModelConnectionViewModelTests
{
    [Fact]
    public async Task Provider_switch_discards_unsaved_key_and_rechecks_saved_key_before_save()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var model = CreateViewModel(settings, credentials);
        model.SetApiKey("synthetic-old-provider-key");
        model.SelectedProviderId = "deepseek";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Saved);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));

        credentials.Values["fgo-pet/provider/openai"] = "synthetic-saved-key";
        model.SelectedProviderId = "openai";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("openai", settings.Saved?.ModelConnection?.ProviderId);
        Assert.True(model.IsKeySaved);

        model.SelectedProviderId = "deepseek";
        Assert.False(model.IsKeySaved);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("openai", settings.Saved?.ModelConnection?.ProviderId);
        Assert.NotEmpty(model.ErrorText);
    }

    [Fact]
    public async Task Switching_provider_discards_pending_key_and_does_not_reuse_saved_key_state()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/provider/openai"] = "fixture-saved-a";
        var handler = new ScriptedHandler();
        var model = CreateViewModel(settings, credentials, handler);
        await model.InitializeAsync();
        Assert.True(model.IsKeySaved);
        model.SetApiKey("fixture-pending-a");

        model.SelectedProviderId = "deepseek";
        Assert.False(model.IsKeySaved);
        await model.TestCommand.ExecuteAsync(null);
        Assert.Equal(0, handler.Calls);
        await model.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(model.ErrorText);
        Assert.Null(settings.Saved);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));
        Assert.Equal("fixture-saved-a", credentials.Values["fgo-pet/provider/openai"]);
    }

    [Fact]
    public async Task Save_rechecks_current_provider_instead_of_trusting_false_key_state()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/provider/deepseek"] = "fixture-saved-b";
        var model = CreateViewModel(settings, credentials);
        await model.InitializeAsync();
        model.SelectedProviderId = "deepseek";
        Assert.False(model.IsKeySaved);

        await model.SaveCommand.ExecuteAsync(null);

        Assert.Empty(model.ErrorText);
        Assert.True(model.IsKeySaved);
        Assert.Equal("deepseek", settings.Saved!.ModelConnection!.ProviderId);
    }

    [Fact]
    public async Task Save_rechecks_credential_that_was_removed_after_initialization()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/provider/openai"] = "fixture-key";
        var model = CreateViewModel(settings, credentials);
        await model.InitializeAsync();
        Assert.True(model.IsKeySaved);
        credentials.Values.Clear();

        await model.SaveCommand.ExecuteAsync(null);

        Assert.False(model.IsKeySaved);
        Assert.NotEmpty(model.ErrorText);
        Assert.Null(settings.Saved);
    }

    [Fact]
    public async Task Initialize_ignores_old_provider_and_can_refresh_current_provider_again()
    {
        var old = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials = new FakeCredentials
        {
            ExistsOverride = target => target.EndsWith("/openai", StringComparison.Ordinal)
                ? old.Task : Task.FromResult(false),
        };
        var model = CreateViewModel(new FakeSettings(), credentials);
        var first = model.InitializeAsync();
        model.SelectedProviderId = "deepseek";
        await model.InitializeAsync();
        old.SetResult(true);
        await first;
        Assert.False(model.IsKeySaved);

        credentials.ExistsOverride = _ => Task.FromResult(true);
        await model.InitializeAsync();
        Assert.True(model.IsKeySaved);
    }

    [Fact]
    public async Task Save_does_not_clear_or_activate_a_new_draft_after_credential_write_finishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettings();
        var credentials = new FakeCredentials { SaveOverride = (_, _) => release.Task };
        var model = CreateViewModel(settings, credentials);
        model.SetApiKey("fixture-pending-a");
        var oldSave = model.SaveCommand.ExecuteAsync(null);
        model.SelectedProviderId = "deepseek";
        model.SetApiKey("fixture-pending-b");
        release.SetResult();
        await oldSave;

        Assert.Null(settings.Saved);
        Assert.False(model.IsKeySaved);
        Assert.Equal("fixture-pending-a", credentials.Values["fgo-pet/provider/openai"]);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));
        credentials.SaveOverride = null;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("fixture-pending-b", credentials.Values["fgo-pet/provider/deepseek"]);
        Assert.Equal("deepseek", settings.Saved!.ModelConnection!.ProviderId);
    }

    [Fact]
    public async Task Background_refresh_does_not_supersede_an_explicit_save()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettings();
        var credentials = new FakeCredentials { SaveOverride = (_, _) => release.Task };
        var handler = new ScriptedHandler();
        var model = CreateViewModel(settings, credentials, handler);
        model.SetApiKey("fixture-key");
        var save = model.SaveCommand.ExecuteAsync(null);

        await model.EnsureModelsAsync();
        Assert.Equal(0, handler.Calls);
        Assert.True(model.IsBusy);
        release.SetResult();
        await save;

        Assert.NotNull(settings.Saved);
        Assert.True(model.IsKeySaved);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Ensure_models_without_credentials_is_silent_and_never_contacts_provider()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var handler = new ScriptedHandler();
        var model = CreateViewModel(settings, credentials, handler);

        await model.EnsureModelsAsync();

        Assert.Equal(0, handler.Calls);
        Assert.Empty(model.ErrorText);
        Assert.False(model.IsBusy);
        Assert.Empty(model.AvailableModels);
        Assert.Null(settings.Saved);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task Ensure_models_coalesces_caches_and_does_not_save_or_select()
    {
        var clock = new FakeTimeProvider();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler { Respond = (_, _) => response.Task };
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var model = CreateViewModel(settings, credentials, handler, clock);
        model.SetApiKey("fixture-key");
        var first = model.EnsureModelsAsync();
        var second = model.EnsureModelsAsync();
        Assert.Equal(1, handler.Calls);
        Assert.True(model.IsBusy);
        response.SetResult(ModelsResponse("available"));
        await Task.WhenAll(first, second);
        await model.EnsureModelsAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal("gpt-4o-mini", model.ModelId);
        Assert.Null(settings.Saved);
        Assert.Empty(credentials.Values);

        handler.Respond = (_, _) => Task.FromResult(ModelsResponse("updated"));
        clock.Advance(ModelConnectionViewModel.ModelsCacheLifetime);
        await model.EnsureModelsAsync();
        Assert.Equal(2, handler.Calls);
        Assert.Equal("updated", Assert.Single(model.AvailableModels).Id);
        await model.RefreshModelsCommand.ExecuteAsync(null);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Failed_automatic_refresh_keeps_same_connection_models_and_throttles_retry()
    {
        var clock = new FakeTimeProvider();
        var handler = new ScriptedHandler();
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials(), handler, clock);
        model.SetApiKey("fixture-key");
        await model.EnsureModelsAsync();
        clock.Advance(ModelConnectionViewModel.ModelsCacheLifetime);
        handler.Respond = (_, _) => throw new HttpRequestException("fixture failure");
        await model.EnsureModelsAsync();
        Assert.Single(model.AvailableModels);
        Assert.NotEmpty(model.ErrorText);
        Assert.DoesNotContain("fixture failure", model.ErrorText);
        await model.EnsureModelsAsync();
        Assert.Equal(2, handler.Calls);

        handler.Respond = (_, _) => Task.FromResult(ModelsResponse("recovered"));
        clock.Advance(TimeSpan.FromSeconds(30));
        await model.EnsureModelsAsync();
        Assert.Equal(3, handler.Calls);
        Assert.Empty(model.ErrorText);
        Assert.Equal("recovered", Assert.Single(model.AvailableModels).Id);
    }

    [Fact]
    public async Task Automatic_timeout_releases_busy_state_and_ignores_late_success()
    {
        var clock = new FakeTimeProvider();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler { Respond = (_, _) => response.Task };
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials(), handler, clock);
        model.SetApiKey("fixture-key");
        var request = model.EnsureModelsAsync();
        clock.Advance(TimeSpan.FromSeconds(15));
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.IsBusy);
        Assert.Contains("超时", model.ErrorText);
        response.SetResult(ModelsResponse("late"));
        await model.EnsureModelsAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Empty(model.AvailableModels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_or_credential_change_discards_old_failure_and_preserves_new_busy_state(bool endpointChanged)
    {
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler { Respond = (_, _) => oldResponse.Task };
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials(), handler);
        model.SetApiKey("fixture-key");
        var first = model.EnsureModelsAsync();
        if (endpointChanged) model.BaseUrl = "https://new.example/v1";
        else model.SetApiKey("fixture-new-key");
        handler.Respond = (_, _) => newResponse.Task;
        var second = model.EnsureModelsAsync();
        oldResponse.SetException(new HttpRequestException("stale failure"));
        await first;
        Assert.True(model.IsBusy);
        Assert.Empty(model.ErrorText);
        newResponse.SetResult(ModelsResponse("new-model"));
        await second;
        Assert.False(model.IsBusy);
        Assert.Empty(model.ErrorText);
        Assert.Equal("new-model", Assert.Single(model.AvailableModels).Id);
    }

    [Fact]
    public async Task Failed_result_for_an_edited_draft_does_not_apply_its_error()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler { Respond = (_, _) => response.Task };
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials(), handler);
        model.SetApiKey("fixture-key");
        var test = model.TestCommand.ExecuteAsync(null);
        model.ModelId = "newer-model";
        response.SetException(new HttpRequestException("stale failure"));
        await test;

        Assert.Empty(model.ErrorText);
        Assert.False(model.IsBusy);
        Assert.Equal("newer-model", model.ModelId);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_shared_draft_refresh()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler { Respond = (_, _) => response.Task };
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials(), handler);
        model.SetApiKey("fixture-key");
        using var caller = new CancellationTokenSource();
        var first = model.EnsureModelsAsync(caller.Token);
        var second = model.EnsureModelsAsync();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(model.IsBusy);
        response.SetResult(ModelsResponse("available"));
        await second;
        Assert.Equal(1, handler.Calls);
        Assert.Single(model.AvailableModels);
    }

    [Fact]
    public void Draft_models_never_authorize_saved_chat_connection()
    {
        var model = CreateViewModel(new FakeSettings(), new FakeCredentials());
        var source = new FgoPet.App.Dialogue.DialogueModelConnectionSource(model);
        model.BaseUrl = "https://draft.example/v1";
        model.AvailableModels = [new ProviderModel("draft-only", "Draft only")];
        Assert.Empty(source.AvailableModels);
        Assert.False(source.IsAvailable("draft-only"));
    }

    [Fact]
    public async Task Context_override_and_output_are_validated_and_saved()
    {
        var settings = new FakeSettings();
        var model = CreateViewModel(settings, new FakeCredentials());
        model.SetApiKey("fixture");
        model.ContextWindowOverrideText = "32768";
        model.MaxOutputTokensText = "4096";
        Assert.Contains("手工设置", model.ContextLimitText);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal(32768, settings.Saved!.ModelConnection!.ContextWindowOverride);
        Assert.Equal(4096, settings.Saved.ModelConnection.MaxOutputTokens);
        model.MaxOutputTokensText = "40000";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(model.ErrorText);
        Assert.Equal(4096, settings.Saved.ModelConnection.MaxOutputTokens);
        model.ContextWindowOverrideText = "";
        model.MaxOutputTokensText = "2048";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Saved.ModelConnection.ContextWindowOverride);
    }

    [Fact]
    public async Task Stale_model_refresh_cannot_apply_capacity_to_another_endpoint()
    {
        var handler = new DelayedRespondingHandler();
        var credentials = new FakeCredentials();
        var catalog = new ProviderCatalog();
        var model = new ModelConnectionViewModel(new FakeSettings(), credentials, catalog,
            new ChatProviderFactory(catalog, credentials, new HttpClient(handler)));
        model.SetApiKey("fixture");
        var refresh = model.RefreshModelsCommand.ExecuteAsync(null);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        model.BaseUrl = "https://other.test/v1";
        handler.Complete();
        await refresh;
        Assert.Empty(model.AvailableModels);
        Assert.Contains("未获取到上限", model.ContextLimitText);
        Assert.Contains("估算", model.ContextLimitText);
    }

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
        var settings = new FakeSettings { Current = DialogueSettings.Defaults with { ModelConnection = null } };
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

    private static ModelConnectionViewModel CreateViewModel(FakeSettings settings, FakeCredentials credentials,
        HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(
            catalog,
            credentials,
            new HttpClient(handler ?? new RespondingHandler()));
        return new ModelConnectionViewModel(settings, credentials, catalog, factory, clock);
    }

    private sealed class FakeSettings : IDialogueSettingsStore
    {
        public DialogueSettings Current { get; set; } = DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        };
        public DialogueSettings? Saved { get; private set; }
        public DialogueSettings Load() => Current;
        public void Save(DialogueSettings settings) { Current = settings; Saved = settings; }
    }

    private sealed class FakeCredentials : ICredentialStore, ICredentialReader
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Func<string, Task<bool>>? ExistsOverride { get; set; }
        public Func<string, string, Task>? SaveOverride { get; set; }

        public async Task SaveAsync(string target, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveOverride is not null) await SaveOverride(target, secret);
            Values[target] = secret;
        }

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExistsOverride?.Invoke(target) ?? Task.FromResult(Values.ContainsKey(target));
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

    private static HttpResponseMessage ModelsResponse(params string[] ids) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { data = ids.Select(id => new { id }).ToArray() }),
    };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(ModelsResponse("available"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Respond(request, cancellationToken);
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
