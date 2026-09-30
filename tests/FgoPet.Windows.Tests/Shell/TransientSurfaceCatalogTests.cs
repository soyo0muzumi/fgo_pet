using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TransientSurfaceCatalogTests
{
    [Fact]
    public async Task Fake_surface_is_available_only_while_its_plugin_is_active()
    {
        var plugin = new FakePlugin();
        var pluginCatalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(pluginCatalog);
        var catalog = new TransientSurfaceCatalog(pluginCatalog, runtime, [plugin]);
        Assert.Empty(catalog.TransientSurfaces);

        Assert.True((await runtime.StartAsync(default)).Succeeded);
        StaRunner.Run(() =>
        {
            Assert.Equal("test.peek", Assert.Single(catalog.TransientSurfaces).Id);
            Assert.IsType<Border>(catalog.CreateView("test.peek"));
        });

        runtime.CloseAdmission();
        Assert.Empty(catalog.TransientSurfaces);
        StaRunner.Run(() => Assert.Throws<InvalidOperationException>(() => catalog.CreateView("test.peek")));
    }

    private sealed class FakePlugin : IFgoPetPlugin, ITransientSurfaceViewFactory
    {
        public PluginManifest Manifest { get; } = new("test.transient", "1.0.0", 1, ImmutableArray<string>.Empty);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with
        {
            TransientSurfaces = [new("test.peek", "Peek", 320, 400)],
        };
        public string SurfaceId => "test.peek";
        public FrameworkElement CreateView() => new Border();
        public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
