using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class BrowserImageStoreTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly ToolScope Scope = new("conversation", "role", "project");
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new ModelConnectionSettings("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"));

    [Fact]
    public Task Capture_has_browser_origin_and_one_per_run_even_after_expiry_until_release() => StaRunner.RunAsync(() =>
    {
        var clock = new Clock(); var selected = new SelectedImageStore(_ => true);
        using var store = new BrowserImageStore(selected, s => s == Scope, () => Route, clock);
        var attachment = store.Accept(Scope, "run", Png, "image/png", () => true, default);
        var image = store.Resolve(attachment.Reference, "role", "conversation", Route, default);
        Assert.True(image.IsBrowserImage); Assert.Equal(1, image.Width); Assert.False(selected.HasSelection);
        Assert.Equal("IMAGE_RUN_LIMIT", store.Check(Scope, "run"));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Throws<SelectedImageException>(() => store.Resolve(attachment.Reference, "role", "conversation", Route, default));
        Assert.Equal("IMAGE_RUN_LIMIT", store.Check(Scope, "run"));
        store.Release("other"); Assert.Equal("IMAGE_RUN_LIMIT", store.Check(Scope, "run"));
        store.Release("run"); Assert.Null(store.Check(Scope, "new-run"));
        store.Dispose(); Assert.Equal("IMAGE_SELECTION_UNAVAILABLE", store.Check(Scope, "new-run"));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("grant")]
    [InlineData("scope")]
    [InlineData("route")]
    [InlineData("selection")]
    [InlineData("release")]
    public Task Captured_image_cannot_cross_revocation_boundaries(string change) => StaRunner.RunAsync(() =>
    {
        var granted = true; var active = true; var model = Route;
        var selected = new SelectedImageStore(_ => true);
        using var store = new BrowserImageStore(selected, _ => active, () => model, TimeProvider.System);
        var attachment = store.Accept(Scope, "run", Png, "image/png", () => granted, default);
        Assert.Throws<SelectedImageException>(() => store.Resolve(attachment.Reference, "other", "conversation", model, default));
        if (change == "grant") granted = false;
        if (change == "scope") active = false;
        if (change == "route") model = Route with { ModelId = "other" };
        if (change == "selection") selected.Grant(Scope, Route, new(Png, 1, 1), "user");
        if (change == "release") store.Release("run");
        Assert.Throws<SelectedImageException>(() => store.Resolve(attachment.Reference, "role", "conversation", model, default));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Invalid_pixels_and_existing_picker_image_fail_before_capture_intake() => StaRunner.RunAsync(() =>
    {
        var selected = new SelectedImageStore(_ => true);
        using var store = new BrowserImageStore(selected, _ => true, () => Route, TimeProvider.System);
        Assert.Throws<BrowserImageException>(() => store.Accept(Scope, "run", [1, 2, 3], "image/png", () => true, default));
        Assert.Null(store.Check(Scope, "run"));
        selected.Grant(Scope, Route, new(Png, 1, 1), "user");
        Assert.Equal("IMAGE_SELECTION_CONFLICT", store.Check(Scope, "run"));
        return Task.CompletedTask;
    });

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
