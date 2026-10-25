using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeCompactedHistoryRegistrationTests
{
    [Fact]
    public async Task Current_history_tools_follow_native_plugin_activation_and_stop()
    {
        // Exercise the actual Dialogue composition without activating unrelated plugins
        // or resolving any current-user settings/database paths.
        var path = Path.Combine(Path.GetTempPath(), $"fgo-history-registration-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection().AddLogging().AddDialogueRuntime();
        services.AddSingleton(new RuntimeDatabase(path, pooling: false));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDialogueSettingsStore, FixtureSettings>();
        services.AddSingleton<IConversationScopeAuthority, InactiveScope>();
        services.AddSingleton(provider => PluginCatalog.Create(provider.GetServices<IFgoPetPlugin>()));
        services.AddSingleton<PluginRuntime>();
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<SqliteCompactedHistoryRepository>(provider.GetRequiredService<ICompactedHistoryRepository>());
        var runtime = provider.GetRequiredService<PluginRuntime>();
        var registry = provider.GetRequiredService<ToolRegistry>();
        var scope = new ToolScope("fixture-conversation", "fixture-role", null);
        Assert.False(registry.TryResolve("history.search", scope, out _));
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        foreach (var name in new[] { "history.search", "history.read" })
        {
            Assert.True(registry.TryResolve(name, scope, out var tool));
            Assert.Equal("firstparty.native-agent", tool.PluginId);
            Assert.Equal(ToolEffect.ReadOnly, tool.Descriptor.Effect);
        }
        Assert.True(registry.TryResolve("experience.search", scope, out _));
        Assert.True(registry.TryResolve("experience.read", scope, out _));
        Assert.True(registry.TryResolve("history.read", scope, out var retained));
        using var arguments = JsonDocument.Parse("{\"messageId\":\"fixture-message\"}");
        Assert.Equal("HISTORY_SCOPE_DENIED", (await retained.Provider.InvokeAsync(new(scope, arguments.RootElement), default)).ErrorCode);
        await runtime.StopAsync();
        Assert.False(registry.TryResolve("history.read", scope, out _));
        Assert.Equal("HISTORY_SCOPE_DENIED", (await retained.Provider.InvokeAsync(new(scope, arguments.RootElement), default)).ErrorCode);
        Assert.False(File.Exists(path)); // Plugin start does not open or migrate storage.
    }

    private sealed class FixtureSettings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults;
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Retained_history_provider_rejects_after_stop_even_when_host_scope_remains_active()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-history-stopped-registration-{Guid.NewGuid():N}.db");
        var scope = new ToolScope("fixture-conversation", "fixture-role", null);
        var authority = new SwitchableScope(scope);
        var repository = new CountingHistoryRepository();
        var services = new ServiceCollection().AddLogging().AddDialogueRuntime();
        services.AddSingleton(new RuntimeDatabase(path, pooling: false));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDialogueSettingsStore, FixtureSettings>();
        services.AddSingleton<IConversationScopeAuthority>(authority);
        services.AddSingleton<ICompactedHistoryRepository>(repository);
        services.AddSingleton(provider => PluginCatalog.Create(provider.GetServices<IFgoPetPlugin>()));
        services.AddSingleton<PluginRuntime>();
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<PluginRuntime>();
        var registry = provider.GetRequiredService<ToolRegistry>();
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        Assert.True(registry.TryResolve("history.read", scope, out var retained));
        using var arguments = JsonDocument.Parse("{\"messageId\":\"fixture-message\"}");
        var invocation = new ToolInvocation(scope, arguments.RootElement);

        Assert.Equal("HISTORY_SCOPE_DENIED", (await retained.Provider.InvokeAsync(invocation, default)).ErrorCode);
        Assert.Equal(0, repository.Reads);
        authority.Active = true;
        var active = await retained.Provider.InvokeAsync(invocation, default);
        Assert.True(active.Success, active.ErrorCode);
        Assert.Equal("original fixture", Assert.Single(active.Payload.GetProperty("items").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(1, repository.Reads);

        await runtime.StopAsync();
        Assert.True(authority.IsActive(scope));
        Assert.False(registry.TryResolve("history.read", scope, out _));
        var stopped = await retained.Provider.InvokeAsync(invocation, default);
        Assert.False(stopped.Success);
        Assert.Equal("HISTORY_SCOPE_DENIED", stopped.ErrorCode);
        Assert.Equal("{}", stopped.Payload.GetRawText());
        Assert.Equal(1, repository.Reads);
        Assert.False(File.Exists(path));
    }

    private sealed class SwitchableScope(ToolScope scope) : IConversationScopeAuthority
    {
        public bool Active { get; set; }
        public ConversationScope? Current => Active ? new(scope.RoleId, scope.ProjectId) : null;
        public bool IsActive(ToolScope candidate) => Active && candidate == scope;
    }

    private sealed class CountingHistoryRepository : ICompactedHistoryRepository
    {
        public int Reads { get; private set; }
        public CompactedHistoryPage Search(ToolScope scope, string query, int afterSequence = 0,
            int maxItems = 8, int maxChars = 6000, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public CompactedHistoryPage Read(ToolScope scope, string messageId, int offset = 0,
            int maxChars = 6000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return new(1, 2, [new(messageId, 1, "user", "completed", DateTimeOffset.UnixEpoch,
                "original fixture", 0, null)]);
        }
    }

    private sealed class InactiveScope : IConversationScopeAuthority
    {
        public ConversationScope? Current => null;
        public bool IsActive(ToolScope scope) => false;
    }
}
