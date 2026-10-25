using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ImageToolsPluginTests
{
    [Fact]
    public async Task Tool_never_accepts_paths_and_emits_only_safe_diagnostics_and_reference()
    {
        var scope = new ToolScope("conversation", "role", null);
        var route = ModelRouteKey.From(new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"));
        var images = new SelectedImageStore(s => s == scope);
        var diagnostics = new List<ImageReadDiagnostic>();
        await using var plugin = new ImageToolsPlugin(images, () => route, diagnostics.Add);
        var empty = new ToolInvocation(scope, JsonSerializer.SerializeToElement(new { }))
            { ExecutionContext = new("run", 1, "call", "key") };
        Assert.Equal("IMAGE_STOPPED", (await plugin.InvokeAsync(empty, default)).ErrorCode);
        await plugin.StartAsync(default);
        Assert.Equal("IMAGE_SELECTION_REQUIRED", (await plugin.InvokeAsync(empty, default)).ErrorCode);
        images.Grant(scope, route, new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1), "user");
        images.Bind(scope, route, "user", "run", default);
        var result = await plugin.InvokeAsync(empty, default);
        Assert.True(result.Success); Assert.NotNull(result.Image);
        Assert.DoesNotContain("base64", result.Payload.GetRawText());
        Assert.Equal("TOOL_INVALID_ARGUMENTS", (await plugin.InvokeAsync(empty with
            { Arguments = JsonSerializer.SerializeToElement(new { path = "private-fixture-path" }) }, default)).ErrorCode);
        Assert.Equal("IMAGE_SCOPE_DENIED", (await plugin.InvokeAsync(empty with { Scope = scope with { ConversationId = "other" } }, default)).ErrorCode);
        Assert.DoesNotContain("private-fixture-path", JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain(result.Image!.Id, JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(diagnostics));
        await Assert.ThrowsAsync<OperationCanceledException>(() => plugin.InvokeAsync(empty, new(true)).AsTask());
        await plugin.StopAsync(default);
        Assert.Equal("IMAGE_STOPPED", (await plugin.InvokeAsync(empty, default)).ErrorCode);
        Assert.Throws<SelectedImageException>(() => images.Read(scope, route, "run", default));
    }
    [Fact]
    public async Task Synchronous_disposal_revokes_images_and_is_safe_to_repeat_before_process_cancellation()
    {
        var scope = new ToolScope("conversation", "role", null);
        var route = ModelRouteKey.From(new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"));
        var images = new SelectedImageStore(s => s == scope);
        using var stopping = new CancellationTokenSource();
        var plugin = new ImageToolsPlugin(images, () => route);
        await plugin.StartAsync(stopping.Token);
        images.Grant(scope, route, new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1), "user");
        images.Bind(scope, route, "user", "run", default);
        var invocation = new ToolInvocation(scope, JsonSerializer.SerializeToElement(new { }))
            { ExecutionContext = new("run", 1, "call", "key") };
        var reference = (await plugin.InvokeAsync(invocation, default)).Image!;
        Assert.NotNull(reference);
        Assert.IsAssignableFrom<IDisposable>(plugin).Dispose();
        Assert.Throws<SelectedImageException>(() => images.Resolve(reference, scope.RoleId, scope.ConversationId, route, default));
        Assert.Equal("IMAGE_STOPPED", (await plugin.InvokeAsync(invocation, default)).ErrorCode);
        plugin.Dispose();
        await plugin.DisposeAsync();
        stopping.Cancel();
        Assert.Throws<SelectedImageException>(() => images.Read(scope, route, "run", default));
        Assert.Equal("IMAGE_STOPPED", (await plugin.InvokeAsync(invocation, default)).ErrorCode);
    }

    [Fact]
    public async Task Image_context_and_tool_require_the_desktop_message_and_its_exact_native_run()
    {
        var scope = new ToolScope("conversation", "role", null);
        var route = ModelRouteKey.From(new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"));
        var images = new SelectedImageStore(s => s == scope);
        await using var plugin = new ImageToolsPlugin(images, () => route);
        await plugin.StartAsync(default);
        images.Grant(scope, route, new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1), "desktop-message");
        var remote = new AgentContextRequest(scope, "remote-run", 1, new("remote-message", "hash"), 1024, []);
        Assert.Empty(await plugin.BuildAsync(remote, default));
        var invocation = new ToolInvocation(scope, JsonSerializer.SerializeToElement(new { }))
            { ExecutionContext = new("remote-run", 1, "call", "key") };
        Assert.False((await plugin.InvokeAsync(invocation, default)).Success);
        Assert.Single(await plugin.BuildAsync(remote with { RunId = "desktop-run", Query = new("desktop-message", "hash") }, default));
        Assert.False((await plugin.InvokeAsync(invocation, default)).Success);
        Assert.False((await plugin.InvokeAsync(invocation with { ExecutionContext = null }, default)).Success);
        Assert.Empty(await plugin.BuildAsync(remote with { Query = new("desktop-message", "hash") }, default));
        Assert.NotNull((await plugin.InvokeAsync(invocation with { ExecutionContext = new("desktop-run", 1, "call", "key") }, default)).Image);
    }

    [Fact]
    public async Task Unsupported_model_returns_explicit_failure_without_fabricated_image_read()
    {
        await using var plugin = new ImageToolsPlugin(new(_ => true),
            () => ModelRouteKey.From(new("custom-openai-compatible", "https://fixture.test/v1", "gpt-4.1-mini")));
        await plugin.StartAsync(default);
        var result = await plugin.InvokeAsync(new(new("c", "r", null), JsonSerializer.SerializeToElement(new { })), default);
        Assert.False(result.Success); Assert.Null(result.Image); Assert.Equal("IMAGE_MODEL_UNSUPPORTED", result.ErrorCode);
    }
}
