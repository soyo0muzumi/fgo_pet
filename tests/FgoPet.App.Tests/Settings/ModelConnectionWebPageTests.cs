using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using FgoPet.App.Providers;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class ModelConnectionWebPageTests
{
    [Fact]
    public async Task Get_returns_full_snapshot_and_does_not_write()
    {
        var settings = new FakeSettings();
        var page = new ModelConnectionWebPage(CreateViewModel(settings, new FakeCredentials()));

        var result = await Send(page, "modelConnection.get", "{\"pageId\":\"ModelConnection\"}");

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var root = document.RootElement;
        Assert.Equal("openai", root.GetProperty("providerId").GetString());
        Assert.NotEmpty(root.GetProperty("providers").EnumerateArray());
        var firstProvider = root.GetProperty("providers").EnumerateArray().First();
        Assert.True(firstProvider.TryGetProperty("id", out _));
        Assert.True(firstProvider.TryGetProperty("displayName", out _));
        Assert.True(firstProvider.TryGetProperty("defaultBaseUrl", out _));
        Assert.Equal("", root.GetProperty("errorText").GetString());
        Assert.False(root.GetProperty("isKeySaved").GetBoolean());
        Assert.Equal("gpt-4o-mini", root.GetProperty("modelId").GetString());
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task SetDraft_updates_whitelisted_fields_and_survives_across_sessions()
    {
        var settings = new FakeSettings();
        var viewModel = CreateViewModel(settings, new FakeCredentials());
        var page = new ModelConnectionWebPage(viewModel);

        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"providerId\",\"value\":\"deepseek\"}")).Success);
        Assert.Equal("deepseek", viewModel.SelectedProviderId);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"baseUrl\",\"value\":\"https://api.deepseek.com/v1\"}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"modelId\",\"value\":\"deepseek-chat\"}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"contextWindowOverride\",\"value\":\"32768\"}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"maxOutputTokens\",\"value\":\"4096\"}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"apiKey\",\"value\":\"secret-value\"}")).Success);

        var session = Assert.IsType<ModelConnectionWebPage>(page.CreateSession());
        Assert.NotSame(page, session);
        var reread = await Send(session, "modelConnection.get", "{\"pageId\":\"ModelConnection\"}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(reread.Payload));
        var root = document.RootElement;
        Assert.Equal("deepseek", root.GetProperty("providerId").GetString());
        Assert.Equal("https://api.deepseek.com/v1", root.GetProperty("baseUrl").GetString());
        Assert.Equal("deepseek-chat", root.GetProperty("modelId").GetString());
        Assert.Equal("32768", root.GetProperty("contextWindowOverrideText").GetString());
        Assert.Equal("4096", root.GetProperty("maxOutputTokensText").GetString());
        Assert.DoesNotContain("secret-value", JsonSerializer.Serialize(reread.Payload));
    }

    [Fact]
    public async Task SetDraft_rejects_unknown_field_extra_property_and_wrong_value_type()
    {
        var page = new ModelConnectionWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()));

        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"other\",\"value\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"baseUrl\",\"value\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"providerId\",\"value\":\"unknown-provider\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"baseUrl\",\"value\":\"x\",\"extra\":true}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\"}")).ErrorCode);
    }

    [Fact]
    public async Task SetShowReasoning_persists_immediately_without_waiting_for_save()
    {
        var settings = new FakeSettings();
        var page = new ModelConnectionWebPage(CreateViewModel(settings, new FakeCredentials()));

        Assert.True(settings.Load().ShowReasoning);
        var result = await Send(page, "modelConnection.setShowReasoning",
            "{\"pageId\":\"ModelConnection\",\"value\":false}");

        Assert.True(result.Success);
        Assert.False(settings.Load().ShowReasoning);
        Assert.False(settings.Saved!.ShowReasoning);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public async Task Save_commits_connection_and_credential_but_test_does_not()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials);
        var page = new ModelConnectionWebPage(viewModel);
        await DraftConnection(page, "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", credentials: "secret-value");

        var save = await Send(page, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}");
        Assert.True(save.Success);
        Assert.Equal("deepseek", settings.Saved!.ModelConnection!.ProviderId);
        Assert.Equal("secret-value", credentials.Values["fgo-pet/provider/deepseek"]);
        Assert.True(viewModel.IsKeySaved);

        var readBack = await Send(page, "modelConnection.get", "{\"pageId\":\"ModelConnection\"}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(readBack.Payload));
        Assert.True(document.RootElement.GetProperty("isKeySaved").GetBoolean());

        var settings2 = new FakeSettings();
        var credentials2 = new FakeCredentials();
        var viewModel2 = CreateViewModel(settings2, credentials2, new RespondingHandler());
        var page2 = new ModelConnectionWebPage(viewModel2);
        await DraftConnection(page2, "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", credentials: "new-key");

        var test = await Send(page2, "modelConnection.test", "{\"pageId\":\"ModelConnection\"}");
        Assert.True(test.Success);
        Assert.Null(settings2.Saved);
        Assert.False(credentials2.Values.ContainsKey("fgo-pet/provider/deepseek"));
        Assert.Contains("尚未保存", viewModel2.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshModels_does_not_persist_connection_or_credential()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials, new RespondingHandler());
        var page = new ModelConnectionWebPage(viewModel);
        await DraftConnection(page, "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", credentials: "new-key");

        var refresh = await Send(page, "modelConnection.refreshModels", "{\"pageId\":\"ModelConnection\"}");
        Assert.True(refresh.Success);
        Assert.Null(settings.Saved);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));
    }

    [Fact]
    public async Task Save_without_key_reports_save_failed_with_view_model_error()
    {
        var settings = new FakeSettings { Current = DialogueSettings.Defaults with { ModelConnection = null } };
        var credentials = new FakeCredentials();
        var page = new ModelConnectionWebPage(CreateViewModel(settings, credentials));
        await DraftConnection(page, "deepseek", "https://api.deepseek.com/v1", "deepseek-chat");

        var save = await Send(page, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}");
        Assert.False(save.Success);
        Assert.Equal("SETTINGS_SAVE_FAILED", save.ErrorCode);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(save.Payload));
        Assert.Contains("请先输入 API Key", document.RootElement.GetProperty("snapshot").GetProperty("errorText").GetString());
        Assert.Null(settings.Saved);
    }

    [Fact]
    public async Task Save_with_invalid_metadata_reports_save_failed()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var page = new ModelConnectionWebPage(CreateViewModel(settings, credentials));
        await DraftConnection(page, "deepseek", baseUrl: string.Empty, modelId: "deepseek-chat", credentials: "secret-value");

        var save = await Send(page, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}");
        Assert.False(save.Success);
        Assert.Equal("SETTINGS_SAVE_FAILED", save.ErrorCode);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(save.Payload));
        Assert.Contains("设置无效", document.RootElement.GetProperty("snapshot").GetProperty("errorText").GetString());
        Assert.Null(settings.Saved);
    }

    [Fact]
    public async Task ClearKey_removes_only_the_current_provider_credential()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/provider/openai"] = "untouched";
        var viewModel = CreateViewModel(settings, credentials);
        var page = new ModelConnectionWebPage(viewModel);
        await DraftConnection(page, "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", credentials: "secret-value");

        Assert.True((await Send(page, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}")).Success);
        Assert.True(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));

        var clear = await Send(page, "modelConnection.clearKey", "{\"pageId\":\"ModelConnection\"}");
        Assert.True(clear.Success);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/provider/deepseek"));
        Assert.Equal("untouched", credentials.Values["fgo-pet/provider/openai"]);
        Assert.False(viewModel.IsKeySaved);
    }

    [Fact]
    public async Task Unknown_command_returns_unknown_command()
    {
        var page = new ModelConnectionWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()));
        Assert.Equal("SETTINGS_UNKNOWN_COMMAND",
            (await Send(page, "modelConnection.bogus", "{\"pageId\":\"ModelConnection\"}")).ErrorCode);
    }

    [Fact]
    public async Task Page_id_mismatch_or_missing_returns_invalid_input()
    {
        var page = new ModelConnectionWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()));
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "modelConnection.get", "{\"pageId\":\"Personalization\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "modelConnection.get", "{\"pageId\":\"\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "modelConnection.get", "null")).ErrorCode);
    }

    [Fact]
    public async Task Test_against_a_stale_draft_does_not_apply_the_result()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var handler = new DelayedRespondingHandler();
        var viewModel = CreateViewModel(settings, credentials, handler);
        var page = new ModelConnectionWebPage(viewModel);
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"apiKey\",\"value\":\"new-key\"}")).Success);

        var test = page.HandleCommandAsync(
            new WebSurfaceMessage("modelConnection.test", null, JsonDocument.Parse("{\"pageId\":\"ModelConnection\"}").RootElement.Clone()),
            CancellationToken.None);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await Send(page, "modelConnection.setDraft",
            "{\"pageId\":\"ModelConnection\",\"field\":\"modelId\",\"value\":\"newer-model\"}")).Success);
        handler.Complete();
        Assert.True((await test).Success);

        var reread = await Send(page, "modelConnection.get", "{\"pageId\":\"ModelConnection\"}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(reread.Payload));
        var root = document.RootElement;
        Assert.Equal("newer-model", root.GetProperty("modelId").GetString());
        Assert.Empty(root.GetProperty("availableModels").EnumerateArray());
        Assert.Contains("草稿已更改", root.GetProperty("statusText").GetString());
        Assert.Null(settings.Saved);
    }

    [Fact]
    public async Task Concurrent_setShowReasoning_and_save_preserve_both_fields()
    {
        var settings = new RaceSettings(DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        });
        var credentials = new FakeCredentials();
        var viewModel = CreateViewModel(settings, credentials);
        var page = new ModelConnectionWebPage(viewModel);
        viewModel.SetApiKey("seed-key");
        Assert.True((await Send(page, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}")).Success);
        Assert.NotNull(settings.Saved);

        var tasks = new List<Task>(16);
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var session = page.CreateSession();
                await Send(session, "modelConnection.setShowReasoning", "{\"pageId\":\"ModelConnection\",\"value\":true}");
            }));
            tasks.Add(Task.Run(async () =>
            {
                var session = page.CreateSession();
                await Send(session, "modelConnection.save", "{\"pageId\":\"ModelConnection\"}");
            }));
        }

        await Task.WhenAll(tasks);
        Assert.NotNull(settings.Saved);
        Assert.True(settings.Saved!.ShowReasoning);
        Assert.NotNull(settings.Saved.ModelConnection);
    }

    private static async Task DraftConnection(ModelConnectionWebPage page, string providerId,
        string baseUrl, string modelId, string? credentials = null)
    {
        Assert.True((await Send(page, "modelConnection.setDraft",
            $"{{\"pageId\":\"ModelConnection\",\"field\":\"providerId\",\"value\":\"{providerId}\"}}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            $"{{\"pageId\":\"ModelConnection\",\"field\":\"baseUrl\",\"value\":\"{baseUrl}\"}}")).Success);
        Assert.True((await Send(page, "modelConnection.setDraft",
            $"{{\"pageId\":\"ModelConnection\",\"field\":\"modelId\",\"value\":\"{modelId}\"}}")).Success);
        if (credentials is not null)
            Assert.True((await Send(page, "modelConnection.setDraft",
                $"{{\"pageId\":\"ModelConnection\",\"field\":\"apiKey\",\"value\":\"{credentials}\"}}")).Success);
    }

    private static ModelConnectionViewModel CreateViewModel(IDialogueSettingsStore settings, FakeCredentials credentials,
        HttpMessageHandler? handler = null)
    {
        var catalog = new ProviderCatalog();
        var factory = new ChatProviderFactory(catalog, credentials,
            handler is null ? new HttpClient() : new HttpClient(handler));
        return new ModelConnectionViewModel(settings, credentials, catalog, factory);
    }

    private static async Task<WebSurfaceCommandResult> Send(ISettingsWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(new WebSurfaceMessage(command, null, document.RootElement.Clone()), CancellationToken.None);
    }

    private sealed class FakeSettings : IDialogueSettingsStore
    {
        public DialogueSettings Current { get; set; } = DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        };
        public DialogueSettings? Saved { get; private set; }
        public int SaveCount { get; private set; }

        public DialogueSettings Load() => Current;
        public void Save(DialogueSettings settings) { Current = settings; Saved = settings; SaveCount++; }
    }

    private sealed class RaceSettings(DialogueSettings initial) : IDialogueSettingsStore
    {
        private readonly object _gate = new();
        private DialogueSettings _value = initial;
        public DialogueSettings? Saved { get; private set; }

        public DialogueSettings Load()
        {
            lock (_gate)
            {
                Thread.Sleep(10);
                return _value;
            }
        }

        public void Save(DialogueSettings settings)
        {
            lock (_gate)
            {
                Thread.Sleep(10);
                _value = settings;
                Saved = settings;
            }
        }
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new[] { new { id = "gpt-4o-mini" } } }),
            });
        }
    }
}
