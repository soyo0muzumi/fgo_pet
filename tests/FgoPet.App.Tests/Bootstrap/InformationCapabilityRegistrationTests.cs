using System.IO;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Plugin.WebSearch;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class InformationCapabilityRegistrationTests
{
    [Fact]
    public async Task Production_search_registration_stays_disabled_without_reading_credentials()
    {
        // Deliberately absent settings; no production state directory is opened or created.
        var directory = Path.Combine(Path.GetTempPath(), "fgo-information-fixture-" + Guid.NewGuid().ToString("N"));
        var credentials = new RecordingCredentials();
        var services = new ServiceCollection().AddLogging().AddNativeCapabilities();
        services.AddSingleton(new AppPaths(directory, Path.Combine(directory, "Packages")));
        services.AddSingleton<ICredentialReader>(credentials);
        await using var provider = services.BuildServiceProvider();
        var plugin = Assert.IsType<WebSearchPlugin>(Assert.Single(provider.GetServices<IFgoPetPlugin>(),
            item => item.Manifest.Id == "firstparty.web-search"));
        await plugin.StartAsync(default);
        var result = await Assert.Single(plugin.Contributions.Tools).InvokeAsync(
            new(new("fixture-conversation", "fixture-role", null), JsonSerializer.SerializeToElement(new { query = "fixture" })), default);

        Assert.False(result.Success);
        Assert.Equal("WEB_SEARCH_NOT_ENABLED", result.ErrorCode);
        Assert.Equal(0, credentials.Reads);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Information_capabilities_and_read_only_understanding_owner_exist_without_memory()
    {
        var services = new ServiceCollection().AddFgoPet([], includeTodo: false, includeFocus: false,
            includeMemory: false, includeSpeech: false, includeAgentBackend: false);
        services.RemoveAll<ICredentialReader>();
        var credentials = new RecordingCredentials();
        services.AddSingleton<ICredentialReader>(credentials);
        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<UserInputBroker>());
        var tools = provider.GetServices<IFgoPetPlugin>().SelectMany(plugin => plugin.Contributions.Tools)
            .Select(tool => tool.Descriptor).ToArray();
        foreach (var name in new[] { "web.search", "experience.search", "experience.read", "understanding.query" })
            Assert.Equal(ToolEffect.ReadOnly, Assert.Single(tools, tool => tool.Name == name).Effect);
        Assert.IsType<UnderstandingService>(provider.GetRequiredService<IUnderstandingReader>());
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public async Task Registered_history_reader_uses_current_role_project_and_conversation_owner()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "fgo-information-fixture-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var database = new RuntimeDatabase(databasePath, pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var services = new ServiceCollection().AddFgoPet([], includeTodo: false, includeFocus: false,
                includeMemory: false, includeSpeech: false, includeAgentBackend: false);
            services.RemoveAll<RuntimeDatabase>();
            services.AddSingleton(database);
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<SqliteConversationRepository>();
            var context = new ContentContextKey("mash", "fixture", "1.0.0", "default", "1", "1");
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, project) in new[] { ("current", "a"), ("previous", "a"), ("other-project", "b") })
            {
                repository.CreateConversation(id, "mash", context, now, project);
                repository.Append(new(id + "-message", id, "mash", ChatMessageRole.User,
                    "fixture history " + id, ChatMessageStatus.Completed, now, context, 1));
            }
            repository.WriteState("LastActiveConversationId:mash", "current", now);
            provider.GetRequiredService<AppRuntime>().SetActiveRole(new("fixture", "default", "1.0.0", "mash"));
            var plugin = Assert.Single(provider.GetServices<IFgoPetPlugin>(), item => item.Manifest.Id == "experience");
            await plugin.StartAsync(default);
            var scope = new ToolScope("current", "mash", "a");
            var search = Assert.Single(plugin.Contributions.Tools, tool => tool.Descriptor.Name == "experience.search");
            var read = Assert.Single(plugin.Contributions.Tools, tool => tool.Descriptor.Name == "experience.read");
            var result = await search.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query = "fixture" })), default);
            Assert.True(result.Success, result.ErrorCode);
            var source = Assert.Single(result.Payload.GetProperty("items").EnumerateArray());
            Assert.Equal("previous", source.GetProperty("conversationId").GetString());
            var reference = JsonSerializer.SerializeToElement(new
            {
                sourceId = source.GetProperty("sourceId").GetString(),
                sourceVersion = source.GetProperty("sourceVersion").GetString()
            });
            Assert.True((await read.InvokeAsync(new(scope, reference), default)).Success);

            repository.WriteState("LastActiveConversationId:mash", "other-project", now);
            var stale = await read.InvokeAsync(new(scope, reference), default);
            Assert.False(stale.Success);
            Assert.Equal("EXPERIENCE_SCOPE_DENIED", stale.ErrorCode);
            Assert.Equal("{}", stale.Payload.GetRawText());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }

    private sealed class RecordingCredentials : ICredentialReader
    {
        public int Reads { get; private set; }
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Fixture credentials must not be read before source authorization.");
        }
    }
}
