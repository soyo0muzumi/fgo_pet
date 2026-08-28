using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.App.Main;
using FgoPet.App.Panels;
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
    public void PortraitWindow_mounts_panel_and_removes_it_from_layout_when_collapsed()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel);
            try
            {
                Assert.False(window.IsAttachedPanelVisible);

                panel.PortraitClick();
                Assert.True(window.IsAttachedPanelVisible);

                panel.PortraitClick();
                Assert.False(window.IsAttachedPanelVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Attached_panel_is_rendered_above_the_portrait_like_phase_0()
    {
        StaRun(() =>
        {
            var window = new PortraitWindow();
            try
            {
                var canvas = Assert.IsType<Canvas>(window.FindName("HostCanvas"));
                var portrait = Assert.IsAssignableFrom<UIElement>(window.FindName("Portrait"));
                var panel = Assert.IsAssignableFrom<UIElement>(window.FindName("PanelHost"));

                Assert.True(canvas.Children.IndexOf(panel) > canvas.Children.IndexOf(portrait));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Opening_and_closing_the_panel_keeps_portrait_and_host_geometry_stable()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 300.25, Top = 100.25 };
            try
            {
                var geometry = PortraitLayout.Calculate(
                    new PortraitSourceGeometry(303, 603, 13, 0, 256, 240, 151, 360),
                    0.5,
                    new Dpi2(2, 2));
                window.PrepareStablePanelLayout(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(2, 2));
                var portraitBefore = window.PortraitScreenBounds;
                var hostBefore = new LogicalRect(window.Left, window.Top, window.Width, window.Height);

                panel.PortraitClick();
                window.ArrangeOverlayPanel(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(2, 2));
                panel.PortraitClick();

                Assert.Equal(portraitBefore, window.PortraitScreenBounds);
                Assert.Equal(hostBefore, new LogicalRect(window.Left, window.Top, window.Width, window.Height));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Portrait_click_and_escape_drive_the_attached_panel_state_machine()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel);
            try
            {
                window.HandlePortraitClick();
                Assert.Equal(Core.Panels.AttachedPanelState.Compact, panel.State);

                panel.DialogueClick();
                window.HandleEscape();
                Assert.Equal(Core.Panels.AttachedPanelState.Compact, panel.State);

                window.HandleEscape();
                Assert.Equal(Core.Panels.AttachedPanelState.Collapsed, panel.State);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Attached_panel_overlays_the_lower_portrait_and_respects_the_work_area_height_cap()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 0, Top = 0 };
            try
            {
                panel.PortraitClick();
                var geometry = PortraitLayout.Calculate(
                    new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 50, 300),
                    0.5,
                    new Dpi2(1, 1));

                var bounds = window.ArrangeOverlayPanel(
                    geometry,
                    new DeviceRect(0, 0, 1000, 800),
                    new Dpi2(1, 1));

                var portrait = window.PortraitScreenBounds;
                Assert.True(bounds.Top >= portrait.Y + geometry.PanelAnchor.Y);
                Assert.True(bounds.Left < portrait.Right && bounds.Right > portrait.X);
                Assert.Equal(220, bounds.Width);
                Assert.Equal(80, bounds.Height);
                var host = Assert.IsType<ContentControl>(window.FindName("PanelHost"));
                Assert.Equal(80, host.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Attached_panel_is_interactive_only_while_it_is_visible()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 0, Top = 0 };
            try
            {
                panel.PortraitClick();
                var geometry = PortraitLayout.Calculate(
                    new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 50, 300),
                    0.5,
                    new Dpi2(1, 1));
                var bounds = window.ArrangeOverlayPanel(
                    geometry,
                    new DeviceRect(0, 0, 1000, 800),
                    new Dpi2(1, 1));
                var localPoint = new Point(
                    bounds.X - window.Left + 1,
                    bounds.Y - window.Top + 1);

                Assert.True(window.IsAttachedPanelHit(localPoint));

                panel.PortraitClick();
                Assert.False(window.IsAttachedPanelHit(localPoint));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Window_idle_tick_collapses_expanded_content_to_compact()
    {
        StaRun(() =>
        {
            var time = new MutableTimeProvider();
            var panel = new AttachedPanelViewModel(time);
            var window = new PortraitWindow(panel);
            try
            {
                panel.PortraitClick();
                panel.DialogueClick();
                panel.PointerLeft();
                time.Now = time.Now.AddSeconds(31);

                window.HandlePanelIdleTick();

                Assert.Equal(Core.Panels.AttachedPanelState.Compact, panel.State);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Saving_with_an_open_panel_persists_the_portrait_bounds_not_the_host_bounds()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 300, Top = 100 };
            var placement = new MemoryPlacementStore();
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
            panel.PortraitClick();
            var geometry = PortraitLayout.Calculate(
                new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 250, 300),
                0.5,
                new Dpi2(1, 1));
            window.ArrangeOverlayPanel(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(1, 1));

            window.Close();

            Assert.NotNull(placement.Value);
            Assert.Equal(300, placement.Value.OffsetX);
            Assert.Equal(100, placement.Value.OffsetY);
            Assert.Equal(150, placement.Value.WindowWidthDip);
            Assert.Equal(300, placement.Value.WindowHeightDip);
        });
    }

    [Fact]
    public void Portrait_local_coordinates_subtract_canvas_offset_without_dividing_wpf_dips_again()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 100, Top = 80 };
            try
            {
                panel.PortraitClick();
                var geometry = PortraitLayout.Calculate(
                    new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 151, 360),
                    0.5,
                    new Dpi2(2, 2));
                window.ArrangeOverlayPanel(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(2, 2));
                var portrait = window.PortraitScreenBounds;
                var pointInWindow = new Point(
                    portrait.X - window.Left + 10,
                    portrait.Y - window.Top + 20);

                var local = window.ToPortraitLocal(pointInWindow);

                Assert.Equal(new Point(10, 20), local);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Coordinator_returns_the_portrait_fully_inside_the_work_area_after_drag()
    {
        StaRun(() =>
        {
            var panel = new AttachedPanelViewModel(TimeProvider.System);
            var window = new PortraitWindow(panel) { Left = 1900, Top = 1100 };
            var controller = new PortraitController(
                new EmptyRepository(),
                new ExpressionResolver(),
                new PortraitSnapshotCache(),
                new Dpi2(1, 1));
            using var coordinator = new PortraitWindowCoordinator(
                window,
                controller,
                new MemoryPlacementStore(),
                new FixedScreenService());
            var geometry = PortraitLayout.Calculate(
                new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 151, 360),
                0.5,
                new Dpi2(1, 1));
            window.ArrangeOverlayPanel(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(1, 1));
            window.MovePortraitToDevice(new DevicePoint(1900, 1100), new Dpi2(1, 1));

            coordinator.ClampPortraitToWorkArea();

            Assert.Equal(new LogicalRect(1850, 900, 150, 300), window.PortraitScreenBounds);
            window.Close();
        });
    }

    [Fact]
    public void Coordinator_uses_initial_monitor_dpi_when_clamping_left_and_right_edges()
    {
        StaRun(() =>
        {
            var window = new PortraitWindow { Left = 950, Top = 100 };
            var controller = new PortraitController(
                new EmptyRepository(),
                new ExpressionResolver(),
                new PortraitSnapshotCache(),
                new Dpi2(2, 2));
            using var coordinator = new PortraitWindowCoordinator(
                window,
                controller,
                new MemoryPlacementStore(),
                new FixedScreenService());
            var geometry = PortraitLayout.Calculate(
                new PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 151, 360),
                0.5,
                new Dpi2(2, 2));
            window.ArrangeOverlayPanel(geometry, new DeviceRect(0, 0, 2000, 1200), new Dpi2(2, 2));
            coordinator.ApplyWindowDpi(new Dpi2(2, 2));

            coordinator.ClampPortraitToWorkArea();

            Assert.Equal(850, window.PortraitScreenBounds.X);
            Assert.Equal(100, window.PortraitScreenBounds.Y);

            window.MovePortraitToDevice(new DevicePoint(-200, 200), new Dpi2(2, 2));
            coordinator.ClampPortraitToWorkArea();

            Assert.Equal(0, window.PortraitScreenBounds.X);
            window.Close();
        });
    }

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
    public void Tray_double_click_requests_restore_without_reusing_the_show_hide_action()
    {
        StaRun(() =>
        {
            using var tray = new TrayService();
            var restores = 0;
            var toggles = 0;
            tray.RestoreRequested += (_, _) => restores++;
            tray.ShowHideRequested += (_, _) => toggles++;

            tray.HandleDoubleClick();

            Assert.Equal(1, restores);
            Assert.Equal(0, toggles);
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

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
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
