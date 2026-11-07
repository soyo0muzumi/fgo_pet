using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class WebStickerPluginTests
{
    [Fact]
    public Task Automatic_collection_requires_optin_bound_desktop_scope_and_current_web_grant() => StaRunner.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "fgopet-web-sticker-plugin-" + Guid.NewGuid().ToString("N"));
        var calls = 0; var active = true; var web = true;
        var library = new ManagedStickerLibrary(root, (_, _) => { calls++; return Task.FromResult<string?>(ManagedStickerLibraryTests.Png(3)); });
        var scope = new ToolScope("chat", "mash_kyrielight", null);
        var notifications = new List<StickerImportDiagnostic>();
        using var off = new WebStickerPlugin(library, false, _ => active, _ => () => web, () => new(true));
        using var plugin = new WebStickerPlugin(library, true, s => s == scope && active, _ => web ? () => web : null, () => new(web), notifications.Add);
        var args = JsonSerializer.SerializeToElement(new { url = "https://images.example/laugh.png", label = "大笑", intent = "laugh" });
        var invocation = new ToolInvocation(scope, args) { ExecutionContext = new("run", 1, "call", "key") };
        var tool = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "sticker.add");
        try
        {
            await off.StartAsync(default); await plugin.StartAsync(default);
            Assert.Empty(off.Contributions.Tools);
            Assert.Empty(off.Contributions.AgentContexts);
            Assert.Equal(ToolEffect.Command, tool.Descriptor.Effect);
            Assert.Equal("STICKER_SCOPE_DENIED", (await tool.InvokeAsync(invocation with { ExecutionContext = null }, default)).ErrorCode);
            active = false;
            Assert.Equal("STICKER_SCOPE_DENIED", (await tool.InvokeAsync(invocation, default)).ErrorCode);
            active = true; web = false;
            Assert.Equal("STICKER_WEB_AUTHORIZATION_REQUIRED", (await tool.InvokeAsync(invocation, default)).ErrorCode);
            Assert.Equal(0, calls); web = true;
            invocation = invocation with { ExecutionContext = invocation.ExecutionContext! with {
                AutomaticAuthorization = ((IToolAutomaticCommandAuthorizationProvider)tool).CaptureAutomaticAuthorization(scope) } };
            var result = await tool.InvokeAsync(invocation, default);
            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal(ToolExecutionState.Committed, result.ExecutionState);
            Assert.True(StickerPolicy.IsWebAssetId(result.Payload.GetProperty("assetId").GetString()));
            Assert.True((await tool.InvokeAsync(invocation, default)).Success);
            Assert.Equal(1, calls);
            var list = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "sticker.list");
            Assert.Single((await list.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { })), default)).Payload.GetProperty("assets").EnumerateArray());
            Assert.Contains(notifications, n => n.Stage == "committed");
            Assert.All(notifications, n => Assert.DoesNotContain("images.example", JsonSerializer.Serialize(n)));
            await plugin.StopAsync(default);
            Assert.False((await tool.InvokeAsync(invocation, default)).Success);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });
}
