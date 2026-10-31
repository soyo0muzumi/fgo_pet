using System.Text;
using FgoPet.Plugin.WebSearch;
using Xunit;

namespace FgoPet.Capability.Tests.WebSearch;

public sealed class WebSearchSettingsTests
{
    [Fact]
    public void Enabled_host_grant_loads_only_an_identifier_and_positive_revision()
    {
        var settings = LoadFixture("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":2,"Provider":"duckduckgo"}""");
        Assert.NotNull(settings);
        Assert.True(settings.Enabled);
        Assert.Equal("8dc9a81a6aac4720a06fe4b936d69e24", settings.AuthorizationId);
        Assert.Equal(2, settings.AuthorizationVersion);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Enabled\":false,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1}")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"\",\"AuthorizationVersion\":1}")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"user@example.org\",\"AuthorizationVersion\":1}")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":0}")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1,\"ApiKey\":\"synthetic-test-key\"}")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1,\"Endpoint\":\"https://other.invalid\"}")]
    [InlineData("{\"Enabled\":true,\"Enabled\":false,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1}")]
    public void Missing_disabled_or_unsafe_host_configuration_denies_access(string content)
        => Assert.Null(LoadFixture(content));

    [Fact]
    public void Oversize_host_configuration_is_not_loaded()
        => Assert.Null(LoadFixture(new string(' ', 4097)));

    [Fact]
    public void Missing_host_configuration_is_unavailable()
        => Assert.Null(WebSearchSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));

    [Theory]
    [InlineData("not json", "WEB_SEARCH_CONFIG_INVALID")]
    [InlineData("{\"Enabled\":true,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1}", "WEB_SEARCH_PROVIDER_CHANGED")]
    [InlineData("{\"Enabled\":false,\"AuthorizationId\":\"8dc9a81a6aac4720a06fe4b936d69e24\",\"AuthorizationVersion\":1,\"Provider\":\"duckduckgo\"}", "WEB_SEARCH_NOT_ENABLED")]
    public void Read_retains_safe_distinct_configuration_reasons(string content, string code)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal(code, WebSearchSettings.Read(path).ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Store_inspection_is_read_only_and_explicit_toggle_creates_and_rotates_grants()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fgopet-search-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "web-search.json");
        var worker = Path.Combine(directory, "worker.exe");
        try
        {
            var store = new WebSearchSettingsStore(path, worker);
            Assert.Equal("WEB_SEARCH_NOT_CONFIGURED", store.GetSnapshot().ErrorCode);
            Assert.False(Directory.Exists(directory));
            var enabled = await store.SetEnabledAsync(true, default);
            Assert.True(enabled.Enabled);
            Assert.Equal("WEB_SEARCH_RUNTIME_MISSING", enabled.ErrorCode);
            var first = store.Load()!;
            File.WriteAllText(worker, "synthetic fixture");
            Assert.True(store.GetAvailability().IsAvailable);
            var disabled = await store.SetEnabledAsync(false, default);
            Assert.Equal("WEB_SEARCH_NOT_ENABLED", disabled.ErrorCode);
            Assert.Null(store.Load());
            await store.SetEnabledAsync(true, default);
            Assert.NotEqual(first.AuthorizationId, store.Load()!.AuthorizationId);
            Assert.True(store.Load()!.AuthorizationVersion > first.AuthorizationVersion);
            Assert.Equal("duckduckgo", store.Load()!.Provider);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Cancelled_toggle_does_not_write_and_explicit_repair_replaces_invalid_configuration()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "broken fixture");
            var store = new WebSearchSettingsStore(path, path + ".exe");
            Assert.Equal("WEB_SEARCH_CONFIG_INVALID", store.GetSnapshot().ErrorCode);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetEnabledAsync(true, cancelled.Token));
            Assert.Equal("broken fixture", File.ReadAllText(path));
            Assert.True((await store.SetEnabledAsync(true, default)).Enabled);
            Assert.NotNull(store.Load());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unreadable_configuration_is_reported_and_never_replaced()
    {
        var path = Path.GetTempFileName();
        const string original = "protected fixture contents";
        try
        {
            File.WriteAllText(path, original);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var store = new WebSearchSettingsStore(path, path + ".exe");
                Assert.Equal("WEB_SEARCH_CONFIG_UNREADABLE", store.GetSnapshot().ErrorCode);
                Assert.Equal("WEB_SEARCH_CONFIG_UNREADABLE", (await store.SetEnabledAsync(true, default)).ErrorCode);
            }
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static WebSearchSettings? LoadFixture(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "fgopet-web-search-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return WebSearchSettings.Load(path);
        }
        finally { File.Delete(path); }
    }
}
