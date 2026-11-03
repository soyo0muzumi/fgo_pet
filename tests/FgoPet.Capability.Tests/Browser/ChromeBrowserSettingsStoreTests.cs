using System.Text.Json;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class ChromeBrowserSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "browser-settings-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "chrome-browser.json");
    private string NodePath => Path.Combine(_directory, "node.exe");
    private string ServerPath => Path.Combine(_directory, "server.js");

    public ChromeBrowserSettingsStoreTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(NodePath, "synthetic runtime");
        File.WriteAllText(ServerPath, "synthetic server");
    }

    [Fact]
    public async Task Missing_disabled_ready_and_missing_dependency_have_distinct_local_states()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        Assert.Equal("BROWSER_NOT_CONFIGURED", store.Read().ErrorCode);
        var disabled = await store.SaveAsync(NodePath, ServerPath, null, false);
        Assert.Equal("BROWSER_NOT_ENABLED", disabled.ErrorCode);
        Assert.False(disabled.Enabled);
        Assert.Equal(NodePath, disabled.Settings!.NodePath);
        Assert.Null(store.LoadEnabled());
        Assert.True((await store.SetEnabledAsync(true)).IsAvailable);
        Assert.NotNull(store.LoadEnabled());
        File.Delete(ServerPath);
        Assert.Equal("BROWSER_DEPENDENCY_MISSING", store.Read().ErrorCode);
        Assert.True(store.Read().Enabled);
        Assert.NotNull(store.Read().Settings);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"Enabled\":true,\"Enabled\":false}")]
    [InlineData("{\"Enabled\":true,\"NodePath\":\"relative\",\"ServerPath\":\"relative\"}")]
    [InlineData("{\"Enabled\":\"true\"}")]
    public void Corrupt_or_ambiguous_configuration_is_not_reported_as_disabled(string contents)
    {
        File.WriteAllText(SettingsPath, contents);
        var result = new ChromeBrowserSettingsStore(SettingsPath).Read();
        Assert.Equal("BROWSER_SETTINGS_INVALID", result.ErrorCode);
        Assert.Null(result.Settings);
        Assert.Null(ChromeBrowserSettings.Load(SettingsPath));
    }

    [Fact]
    public void Read_failure_is_separate_from_missing_configuration()
    {
        Directory.CreateDirectory(SettingsPath);
        Assert.Equal("BROWSER_SETTINGS_UNREADABLE", new ChromeBrowserSettingsStore(SettingsPath).Read().ErrorCode);
    }

    [Fact]
    public async Task Unknown_profile_is_distinct_and_can_be_corrected_without_editing_json()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        var missingProfile = Path.Combine(_directory, "missing-profile");
        Assert.Equal("BROWSER_PROFILE_MISSING", (await store.SaveAsync(NodePath, ServerPath, missingProfile, true)).ErrorCode);
        Assert.True((await store.SaveAsync(NodePath, ServerPath, null, true)).IsAvailable);
    }

    [Fact]
    public async Task Explicit_browser_enable_grants_images_and_reenable_rotates_authority()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        var first = (await store.SaveAsync(NodePath, ServerPath, null, true)).Settings!;
        Assert.True(first.VisualEnabled);
        Assert.True(Guid.TryParseExact(first.AuthorizationId, "N", out _));
        var disabled = await store.SetEnabledAsync(false);
        Assert.False(disabled.Settings!.VisualEnabled);
        var reenabled = await store.SetEnabledAsync(true);
        Assert.True(reenabled.Settings!.VisualEnabled);
        Assert.NotEqual(first.AuthorizationId, reenabled.Settings.AuthorizationId);
        Assert.NotEqual(disabled.Settings.AuthorizationId, reenabled.Settings.AuthorizationId);
    }

    [Fact]
    public async Task Changing_profile_revokes_visual_until_browser_grant_is_reconfirmed()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        await store.SaveAsync(NodePath, ServerPath, null, true);
        var before = store.Read().Settings!;
        var same = (await store.SaveAsync(NodePath, ServerPath, null, true)).Settings!;
        Assert.True(same.VisualEnabled);
        Assert.NotEqual(before.AuthorizationId, same.AuthorizationId);
        var changed = (await store.SaveAsync(NodePath, ServerPath, _directory, true)).Settings!;
        Assert.False(changed.VisualEnabled);
        Assert.NotEqual(same.VisualGrantId, changed.VisualGrantId);
        Assert.True((await store.SetEnabledAsync(true)).Settings!.VisualEnabled);
    }

    [Fact]
    public async Task Existing_text_only_grant_requires_explicit_upgrade()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        var initial = (await store.SaveAsync(NodePath, ServerPath, null, true)).Settings!;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(initial with { VisualEnabled = false }));
        Assert.False((await store.SaveAsync(NodePath, ServerPath, null, enabled: null)).Settings!.VisualEnabled);
        Assert.True((await store.SetEnabledAsync(true)).Settings!.VisualEnabled);
    }

    [Fact]
    public async Task Cancelled_save_and_invalid_paths_leave_existing_configuration_unchanged()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        await store.SaveAsync(NodePath, ServerPath, null, true);
        var bytes = File.ReadAllBytes(SettingsPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(NodePath, ServerPath, null, false, cancellation.Token).AsTask());
        Assert.Equal("BROWSER_SETTINGS_INVALID", (await store.SaveAsync("relative", ServerPath, null, true)).ErrorCode);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Visual_dialog_cannot_replace_a_grant_while_the_base_settings_writer_owns_it()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        await store.SaveAsync(NodePath, ServerPath, null, true);
        var before = File.ReadAllBytes(SettingsPath);
        var writer = ChromeBrowserSettings.GetWriteGate(SettingsPath);
        await writer.WaitAsync();
        try
        {
            Assert.False(new ChromeBrowserVisualAccess(SettingsPath).TrySetEnabled(true));
            Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        }
        finally { writer.Release(); }
        Assert.True(new ChromeBrowserVisualAccess(SettingsPath).TrySetEnabled(true));
    }

    [Fact]
    public async Task Queued_advanced_setup_save_does_not_restore_a_concurrently_disabled_grant()
    {
        var store = new ChromeBrowserSettingsStore(SettingsPath);
        var enabled = (await store.SaveAsync(NodePath, ServerPath, null, true)).Settings!;
        var writer = ChromeBrowserSettings.GetWriteGate(SettingsPath);
        await writer.WaitAsync();
        Task<ChromeBrowserSetupState> pending;
        try
        {
            pending = store.SaveAsync(NodePath, ServerPath, _directory, enabled: null).AsTask();
            Assert.False(pending.IsCompleted);
            // Another explicit disable is already inside its write section while Save setup queues.
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(enabled with
            {
                Enabled = false,
                AuthorizationId = Guid.NewGuid().ToString("N"),
            }));
        }
        finally { writer.Release(); }
        var result = await pending;
        Assert.False(result.Enabled);
        Assert.Equal("BROWSER_NOT_ENABLED", result.ErrorCode);
        Assert.Equal(_directory, result.Settings!.UserDataDirectory);
        Assert.False(store.Read().Enabled);
    }

    [Fact]
    public void Older_valid_configuration_remains_readable_without_grant_migration()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Enabled = true, NodePath, ServerPath }));
        Assert.NotNull(ChromeBrowserSettings.Load(SettingsPath));
        Assert.True(new ChromeBrowserSettingsStore(SettingsPath).Read().IsAvailable);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
