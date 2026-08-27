using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.App.Main;
using FgoPet.App.Portraits;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Windowing;
using Xunit;

namespace FgoPet.Windows.Tests.Windowing;

[Trait("Category", "WindowsIntegration")]
public sealed class PortraitWindowIntegrationTests
{
    [Fact]
    public void PortraitWindow_presents_a_validated_snapshot_on_the_STA_thread()
    {
        StaRun(() =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "fgo-pet-win-" + Guid.NewGuid().ToString("N"));
            try
            {
                var body = CreatePng(303, 603, 255);
                var expression = CreatePng(256, 240, 200);
                var bundle = WriteBundle(temp, body, expression);
                var manifest = AppearanceManifestReader.Read(bundle.ManifestPath);
                var validated = AppearanceValidator.Validate(manifest, bundle.Root).Value!;
                var snapshot = BitmapAssetLoader.LoadValidated(validated);
                var geometry = PortraitLayout.Calculate(snapshot.SourceGeometry, 0.50, new Dpi2(2.0, 2.0));

                var window = new PortraitWindow();
                try
                {
                    window.Present(snapshot, geometry);
                    Assert.Same(snapshot.Body, window.PortraitView.BodySourceForTest);
                    Assert.Equal(geometry.LogicalSize.Width, window.Width, precision: 4);
                    Assert.Equal(geometry.LogicalSize.Height, window.Height, precision: 4);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        });
    }

    [Fact]
    public void TrayService_creates_and_disposes_without_events()
    {
        StaRun(() =>
        {
            var tray = new TrayService();
            tray.Dispose();
        });
    }

    [Fact]
    public void WindowsScreenLayoutService_enumerates_a_primary_monitor_with_positive_dpi()
    {
        var service = new WindowsScreenLayoutService();

        var monitors = service.GetMonitors();

        var primary = Assert.Single(monitors.Where(monitor => monitor.IsPrimary));
        Assert.True(primary.WorkArea.Width > 0);
        Assert.True(primary.WorkArea.Height > 0);
        var dpi = service.GetDpi(primary.Id);
        Assert.True(dpi.X > 0);
        Assert.True(dpi.Y > 0);
    }

    [Fact]
    public void PortraitWindowCoordinator_restores_saved_placement()
    {
        StaRun(() =>
        {
            var window = new PortraitWindow();
            var placement = new MemoryPlacementStore
            {
                Value = new WindowPlacement("display", 100, 200, 2, 2, 150, 300),
            };
            var controller = new PortraitController(
                new EmptyRepository(),
                new ExpressionResolver(),
                new PortraitSnapshotCache(),
                new Dpi2(1, 1));
            using var coordinator = new PortraitWindowCoordinator(
                window,
                controller,
                placement,
                new FixedScreenService());

            coordinator.RestorePlacement();

            Assert.Equal(200, window.Left);
            Assert.Equal(400, window.Top);
            Assert.Equal(300, window.Width);
            Assert.Equal(600, window.Height);
            window.Close();
        });
    }

    private static (string Root, string ManifestPath) WriteBundle(string root, byte[] body, byte[] expression)
    {
        Directory.CreateDirectory(Path.Combine(root, "runtime", "expressions"));
        File.WriteAllBytes(Path.Combine(root, "runtime", "full_body.png"), body);
        File.WriteAllBytes(Path.Combine(root, "runtime", "expressions", "r01c01.png"), expression);

        var semantics = new JsonObject();
        foreach (var key in Core.Portraits.ExpressionSemanticKeys.Core)
        {
            semantics[key] = "r01c01";
        }
        var manifest = new JsonObject
        {
            ["schema_version"] = 3,
            ["appearance_id"] = "casual",
            ["assets"] = new JsonArray
            {
                Asset("body", "full_body", "runtime/full_body.png", Sha256(body)),
                Asset("expression", "r01c01", "runtime/expressions/r01c01.png", Sha256(expression)),
            },
            ["composition"] = new JsonObject
            {
                ["body_id"] = "full_body",
                ["default_expression_id"] = "r01c01",
                ["overlay_offset"] = new JsonObject { ["x"] = 13, ["y"] = 0 },
                ["overlay_size"] = new JsonObject { ["width"] = 256, ["height"] = 240 },
                ["panel_anchor"] = new JsonObject { ["x"] = 151, ["y"] = 360 },
                ["default_scale"] = 0.5,
            },
            ["expression_semantics"] = semantics,
            ["fallback"] = new JsonObject(),
        };
        var manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (root, manifestPath);
    }

    private static JsonObject Asset(string type, string id, string path, string hash) => new()
    {
        ["type"] = type,
        ["stable_id"] = id,
        ["path"] = path,
        ["sha256"] = hash,
    };

    private static string Sha256(byte[] content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(content);
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] CreatePng(int width, int height, byte alpha)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            pixels[(index * 4) + 3] = alpha;
        }
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class MemoryPlacementStore : IWindowPlacementStore
    {
        public string Location => "memory";
        public WindowPlacement? Value { get; set; }
        public WindowPlacement? Load() => Value;
        public void Save(WindowPlacement placement) => Value = placement;
    }

    private sealed class FixedScreenService : IScreenLayoutService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() =>
            [new MonitorInfo("display", new DeviceRect(0, 0, 2000, 1200), true)];
        public Dpi2 GetDpi(string monitorId) => new(1, 1);
    }

    private sealed class EmptyRepository : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledServant>>([]);
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
