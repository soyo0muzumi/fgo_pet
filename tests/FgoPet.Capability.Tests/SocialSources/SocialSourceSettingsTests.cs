using System.Text.Json;
using FgoPet.Plugin.SocialSources;
using Xunit;

namespace FgoPet.Capability.Tests.SocialSources;

/// <summary>
/// Grant and tool-contract tests. Every case uses a temporary directory; nothing touches the network,
/// creates production state or reads credentials.
/// </summary>
public sealed class SocialSourceSettingsTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "fgo-social-fixture-" + Guid.NewGuid().ToString("N"));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void Missing_grant_is_not_configured_and_creates_no_directory()
    {
        var directory = TempDirectory();
        var store = new SocialSourceSettingsStore(directory, SocialSourceSettings.Bilibili);

        var snapshot = store.GetSnapshot();

        Assert.False(snapshot.Available);
        Assert.Equal("SOCIAL_NOT_CONFIGURED", snapshot.ErrorCode);
        Assert.False(store.GetAvailability().IsAvailable);
        Assert.Null(store.Load());
        // Inspection must not create the state directory.
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":1,"Platform":"bilibili"}""", true, null)]
    [InlineData("""{"Enabled":false,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":1,"Platform":"bilibili"}""", false, "SOCIAL_NOT_ENABLED")]
    public void Valid_grant_reports_expected_state(string content, bool available, string? expectedError)
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "social-bilibili.json");
            Write(path, content);
            var snapshot = new SocialSourceSettingsStore(directory, SocialSourceSettings.Bilibili).GetSnapshot();
            Assert.Equal(available, snapshot.Available);
            Assert.Equal(expectedError, snapshot.ErrorCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    // extra field (an API key or endpoint must never be accepted)
    [InlineData("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":1,"Platform":"bilibili","ApiKey":"x"}""")]
    // unknown platform
    [InlineData("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":1,"Platform":"weibo"}""")]
    // revision must be positive
    [InlineData("""{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":0,"Platform":"bilibili"}""")]
    // identifier must be a non-empty Guid in "N" form
    [InlineData("""{"Enabled":true,"AuthorizationId":"not-a-guid","AuthorizationVersion":1,"Platform":"bilibili"}""")]
    [InlineData("""{"Enabled":true,"AuthorizationId":"00000000000000000000000000000000","AuthorizationVersion":1,"Platform":"bilibili"}""")]
    // malformed shapes
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"Enabled":"yes"}""")]
    public void Invalid_grant_is_rejected(string content)
    {
        var directory = TempDirectory();
        try
        {
            Write(Path.Combine(directory, "social-bilibili.json"), content);
            var snapshot = new SocialSourceSettingsStore(directory, SocialSourceSettings.Bilibili).GetSnapshot();
            Assert.False(snapshot.Available);
            Assert.Equal("SOCIAL_CONFIG_INVALID", snapshot.ErrorCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void A_grant_written_for_another_platform_does_not_authorize_this_one()
    {
        var directory = TempDirectory();
        try
        {
            // Same identifier, but the file was written for Xiaohongshu and has been copied over.
            Write(Path.Combine(directory, "social-bilibili.json"),
                """{"Enabled":true,"AuthorizationId":"8dc9a81a6aac4720a06fe4b936d69e24","AuthorizationVersion":1,"Platform":"xiaohongshu"}""");

            var snapshot = new SocialSourceSettingsStore(directory, SocialSourceSettings.Bilibili).GetSnapshot();

            Assert.False(snapshot.Available);
            Assert.Equal("SOCIAL_CONFIG_INVALID", snapshot.ErrorCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Oversize_grant_file_is_not_loaded()
    {
        var directory = TempDirectory();
        try
        {
            Write(Path.Combine(directory, "social-bilibili.json"), new string('x', 4097));
            var snapshot = new SocialSourceSettingsStore(directory, SocialSourceSettings.Bilibili).GetSnapshot();
            Assert.False(snapshot.Available);
            Assert.Equal("SOCIAL_CONFIG_INVALID", snapshot.ErrorCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Explicit_toggle_creates_and_rotates_a_grant_for_its_own_platform()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SocialSourceSettingsStore(directory, SocialSourceSettings.Xiaohongshu);

            var enabled = await store.SetEnabledAsync(true, default);
            Assert.True(enabled.Available);
            var first = store.Load();
            Assert.NotNull(first);
            Assert.Equal(SocialSourceSettings.Xiaohongshu, first!.Platform);
            Assert.True(first.Enabled);

            var disabled = await store.SetEnabledAsync(false, default);
            Assert.False(disabled.Available);
            Assert.Equal("SOCIAL_NOT_ENABLED", disabled.ErrorCode);
            Assert.Null(store.Load());

            await store.SetEnabledAsync(true, default);
            var second = store.Load();
            Assert.NotNull(second);
            Assert.NotEqual(first.AuthorizationId, second!.AuthorizationId);
            Assert.True(second.AuthorizationVersion > first.AuthorizationVersion);

            // The neighbouring platform's file was never written by this store.
            Assert.False(File.Exists(Path.Combine(directory, "social-bilibili.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Cancelled_toggle_writes_nothing()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SocialSourceSettingsStore(directory, SocialSourceSettings.Xiaoheihe);
            using var cancel = new CancellationTokenSource();
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetEnabledAsync(true, cancel.Token));
            Assert.False(File.Exists(store.Path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
