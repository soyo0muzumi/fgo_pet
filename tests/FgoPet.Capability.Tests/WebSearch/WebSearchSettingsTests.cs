using System.Text;
using FgoPet.Plugin.WebSearch;
using Xunit;

namespace FgoPet.Capability.Tests.WebSearch;

public sealed class WebSearchSettingsTests
{
    [Fact]
    public void Enabled_host_grant_loads_only_an_identifier_and_positive_revision()
    {
        var settings = LoadFixture("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":2}""");
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
