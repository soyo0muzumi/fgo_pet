using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class DialogueContextBudgetEndToEndTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-context-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(8192, false)]
    [InlineData(32768, true)]
    [InlineData(131072, true)]
    public async Task Full_chinese_input_and_tools_are_counted_before_real_http(int window, bool sent)
    {
        var handler = new Handler();
        var settings = new Settings(new("test", "https://fixture.test/v1", "same",
            contextWindowOverride: window, maxOutputTokens: 2048));
        var userText = new string('汉', 6000);
        var result = await Create(handler, settings).SendAsync("mash", userText, default);
        Assert.Equal(sent ? ConversationSendStatus.Completed : ConversationSendStatus.Failed, result.Status);
        Assert.Equal(sent ? 1 : 0, handler.Posts);
        if (!sent) return;
        using var body = JsonDocument.Parse(handler.Payload!);
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("auto", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.NotEmpty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Contains(userText, body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
    }

    [Fact]
    public async Task Unsupported_output_limit_does_not_drop_limit_or_retry_as_tools_failure()
    {
        var handler = new Handler { RejectOutput = true };
        var settings = new Settings(new("test", "https://fixture.test/v1", "same", contextWindowOverride: 32768));
        var result = await Create(handler, settings).SendAsync("mash", "hello", default);
        Assert.Equal(ConversationSendStatus.ConfigurationRequired, result.Status);
        Assert.Equal(1, handler.Posts);
        Assert.True(settings.Load().ModelConnection!.ToolsSupported);
    }

    private ConversationOrchestrator Create(Handler handler, Settings settings)
    {
        var database = new RuntimeDatabase(_path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var provider = new OpenAiCompatibleChatProvider("test", new Uri("https://fixture.test/v1"), "same",
            new Credentials(), new HttpClient(handler));
        return new(new Resolver(provider), new Content(), new SqliteConversationRepository(database),
            new SqliteMemoryRepository(database), new PromptComposer(), TimeProvider.System, settings);
    }

    private sealed class Settings(ModelConnectionSettings connection) : IDialogueSettingsStore
    {
        private DialogueSettings _settings = DialogueSettings.Defaults with { ModelConnection = connection };
        public DialogueSettings Load() => _settings;
        public void Save(DialogueSettings value) => _settings = value;
    }
    private sealed class Resolver(IChatProvider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Credentials : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>("fixture");
    }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(new("mash", "test", "1.0.0", "default", "1", "1"),
                new PersonaBundle("mash", "test", "1.0.0", "1", "hello", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string? Payload { get; private set; }
        public bool RejectOutput { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Posts++;
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (RejectOutput) return new(HttpStatusCode.BadRequest) { Content = new StringContent(
                """{"error":{"code":"unsupported_parameter","param":"max_tokens","message":"unsupported parameter"}}""") };
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream") };
        }
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
