using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using FgoPet.App.Portraits.Live2D;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class Live2DProviderTests
{
    [Theory]
    [InlineData(1.0, 242)]
    [InlineData(2.0, 483)]
    public void Mash_viewport_keeps_portrait_height_and_fits_full_width(double dpiScale, int expectedDeviceWidth)
    {
        var staticGeometry = PortraitLayout.Calculate(
            new PortraitSourceGeometry(303, 603, 13, 0, 256, 240, 151, 360),
            0.5, new Dpi2(dpiScale, dpiScale));

        var liveGeometry = Live2DPortraitController.MashGeometry(staticGeometry);

        Assert.Equal(staticGeometry.LogicalSize.Height, liveGeometry.LogicalSize.Height);
        Assert.Equal(241.5, liveGeometry.LogicalSize.Width);
        Assert.Equal(expectedDeviceWidth, liveGeometry.DeviceSize.Width);
        Assert.InRange(Math.Abs(liveGeometry.BottomAnchorDevice.X - liveGeometry.DeviceSize.Width / 2), 0, 1);
    }

    [Fact]
    public void Hit_mask_maps_top_left_pixels_and_rejects_invalid_messages()
    {
        var bits = new byte[64 * 96 / 8];
        bits[0] = 1;
        bits[^1] = 0x80;
        var mask = Live2DHitMask.Parse(64, 96, Convert.ToBase64String(bits));
        var geometry = PortraitLayout.Calculate(new PortraitSourceGeometry(640, 960, 0, 0, 1, 1, 320, 480), 0.5, new Dpi2(1, 1));

        Assert.NotNull(mask);
        Assert.True(mask.IsHit(new Point(1, 1), geometry));
        Assert.False(mask.IsHit(new Point(10, 1), geometry));
        Assert.True(mask.IsHit(new Point(319, 479), geometry));
        Assert.False(mask.IsHit(new Point(-1, 1), geometry));
        Assert.Null(Live2DHitMask.Parse(64, 96, "invalid"));
        Assert.Null(Live2DHitMask.Parse(1000, 96, Convert.ToBase64String(bits)));
    }

    [Fact]
    public async Task Missing_live2d_resources_keep_static_frame_and_actions()
    {
        var staticSurface = new StaticSurface();
        using var provider = new Live2DPortraitController(staticSurface, new EmptyRepository(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var changes = 0;
        provider.StateChanged += (_, _) => changes++;

        await provider.ActivateAsync(new PortraitSelection("pkg", "casual"), CancellationToken.None);
        provider.SetExpression(ExpressionSemantic.Happy);
        provider.SetScale(0.6);
        provider.OnPortraitTap();

        Assert.NotNull(provider.CurrentFrame);
        Assert.Equal(ExpressionSemantic.Happy, staticSurface.Semantic);
        Assert.Equal(0.6, staticSurface.Scale);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void Session_stages_runtime_and_model_then_dispose_removes_its_directory()
    {
        using var pack = new Live2DPack();

        var session = Live2DSession.TryCreate(pack.AppearanceRoot, pack.Manifest, pack.RuntimeRoot, pack.SessionsRoot, CancellationToken.None);

        Assert.NotNull(session);
        Assert.True(File.Exists(Path.Combine(session!.Root, "index.html")));
        Assert.True(File.Exists(Path.Combine(session.Root, "app.js")));
        Assert.True(File.Exists(Path.Combine(session.Root, "live2dcubismcore.js")));
        Assert.True(File.Exists(Path.Combine(session.Root, "Resources", "mash_casual", "mash_casual.model3.json")));
        Assert.True(File.Exists(Path.Combine(session.Root, "Resources", "mash_casual", "mash_casual.moc3")));
        Assert.True(File.Exists(Path.Combine(session.Root, "Resources", "mash_casual", "texture.png")));

        var staged = session.Root;
        session.Dispose();
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public void Missing_runtime_file_falls_back_without_staging_a_session()
    {
        using var pack = new Live2DPack();
        File.Delete(Path.Combine(pack.RuntimeRoot, "app.js"));

        Assert.Null(Live2DSession.TryCreate(pack.AppearanceRoot, pack.Manifest, pack.RuntimeRoot, pack.SessionsRoot, CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(pack.SessionsRoot));
    }

    [Fact]
    public void Tampered_model_resource_falls_back_without_staging_a_session()
    {
        using var pack = new Live2DPack();
        File.WriteAllBytes(Path.Combine(pack.AppearanceRoot, "live2d", "mash_casual.moc3"), "tampered"u8.ToArray());

        Assert.Null(Live2DSession.TryCreate(pack.AppearanceRoot, pack.Manifest, pack.RuntimeRoot, pack.SessionsRoot, CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(pack.SessionsRoot));
    }

    [Fact]
    public async Task Repeated_activation_keeps_one_session_and_dispose_leaves_no_session()
    {
        using var pack = new Live2DPack();
        var provider = new Live2DPortraitController(new StaticSurface(), new PackRepository(pack.Location),
            pack.RuntimeRoot, pack.SessionsRoot, pack.ProfileRoot);

        await provider.ActivateAsync(new PortraitSelection("pkg", "casual"), CancellationToken.None);
        Assert.Single(Directory.EnumerateDirectories(pack.SessionsRoot));
        Assert.NotNull(provider.CurrentFrame);

        await provider.ActivateAsync(new PortraitSelection("pkg", "casual"), CancellationToken.None);
        Assert.Single(Directory.EnumerateDirectories(pack.SessionsRoot));

        provider.Dispose();
        provider.Dispose();
        Assert.Empty(Directory.EnumerateDirectories(pack.SessionsRoot));
        Assert.Throws<ObjectDisposedException>(() => provider.CreateView());
    }

    [Fact]
    public async Task Unreadable_appearance_manifest_keeps_the_static_frame()
    {
        using var pack = new Live2DPack();
        File.Delete(Path.Combine(pack.AppearanceRoot, "manifest.json"));
        var staticSurface = new StaticSurface();
        using var provider = new Live2DPortraitController(staticSurface, new PackRepository(pack.Location),
            pack.RuntimeRoot, pack.SessionsRoot, pack.ProfileRoot);

        await provider.ActivateAsync(new PortraitSelection("pkg", "casual"), CancellationToken.None);

        Assert.Equal(1, staticSurface.Activations);
        Assert.NotNull(provider.CurrentFrame);
        Assert.Empty(Directory.EnumerateDirectories(pack.SessionsRoot));
    }

    /// <summary>Synthetic data-only Cubism pack plus a stub runtime; no real model assets are used.</summary>
    private sealed class Live2DPack : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-pet-live2d-" + Guid.NewGuid().ToString("N"));

        public Live2DPack()
        {
            AppearanceRoot = Path.Combine(_root, "appearance");
            RuntimeRoot = Path.Combine(_root, "runtime");
            SessionsRoot = Path.Combine(_root, "sessions");
            ProfileRoot = Path.Combine(_root, "profile");
            Directory.CreateDirectory(SessionsRoot);

            var body = "body"u8.ToArray();
            var expression = "expression"u8.ToArray();
            var model = Encoding.UTF8.GetBytes(
                """{"Version":3,"FileReferences":{"Moc":"mash_casual.moc3","Textures":["texture.png"]}}""");
            var moc = "MOC3"u8.ToArray();
            var texture = "png"u8.ToArray();

            Write(Path.Combine(AppearanceRoot, "body.png"), body);
            Write(Path.Combine(AppearanceRoot, "expression.png"), expression);
            Write(Path.Combine(AppearanceRoot, "live2d", "mash_casual.model3.json"), model);
            Write(Path.Combine(AppearanceRoot, "live2d", "mash_casual.moc3"), moc);
            Write(Path.Combine(AppearanceRoot, "live2d", "texture.png"), texture);
            Write(Path.Combine(RuntimeRoot, "index.html"), "<html></html>"u8.ToArray());
            Write(Path.Combine(RuntimeRoot, "app.js"), "// runtime"u8.ToArray());
            Write(Path.Combine(RuntimeRoot, "live2dcubismcore.js"), "// core"u8.ToArray());

            var semantics = new JsonObject();
            foreach (var key in ExpressionSemanticKeys.Core) semantics[key] = "neutral";
            var fallback = new JsonObject();
            foreach (var key in ExpressionSemanticKeys.Core.Where(key => key != ExpressionSemanticKeys.Neutral)) fallback[key] = "neutral";
            var manifest = new JsonObject
            {
                ["schema_version"] = 3,
                ["appearance_id"] = "casual",
                ["assets"] = new JsonArray
                {
                    new JsonObject { ["type"] = "body", ["stable_id"] = "full_body", ["path"] = "body.png", ["sha256"] = Hash(body) },
                    new JsonObject { ["type"] = "expression", ["stable_id"] = "neutral", ["path"] = "expression.png", ["sha256"] = Hash(expression) },
                },
                ["composition"] = new JsonObject
                {
                    ["body_id"] = "full_body",
                    ["default_expression_id"] = "neutral",
                    ["overlay_offset"] = new JsonObject { ["x"] = 13, ["y"] = 0 },
                    ["overlay_size"] = new JsonObject { ["width"] = 256, ["height"] = 240 },
                    ["panel_anchor"] = new JsonObject { ["x"] = 151, ["y"] = 360 },
                    ["default_scale"] = 0.5,
                },
                ["expression_semantics"] = semantics,
                ["fallback"] = fallback,
                ["live2d"] = new JsonObject
                {
                    ["model_path"] = "live2d/mash_casual.model3.json",
                    ["files"] = new JsonArray
                    {
                        new JsonObject { ["path"] = "live2d/mash_casual.model3.json", ["sha256"] = Hash(model) },
                        new JsonObject { ["path"] = "live2d/mash_casual.moc3", ["sha256"] = Hash(moc) },
                        new JsonObject { ["path"] = "live2d/texture.png", ["sha256"] = Hash(texture) },
                    },
                },
            };
            Write(Path.Combine(AppearanceRoot, "manifest.json"), Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Manifest = AppearanceManifestReader.Read(Path.Combine(AppearanceRoot, "manifest.json"));
            Location = new AppearanceLocation(new PackIdentity("pkg", "1.0.0"), "casual", AppearanceRoot);
        }

        public string AppearanceRoot { get; }
        public string RuntimeRoot { get; }
        public string SessionsRoot { get; }
        public string ProfileRoot { get; }
        public AppearanceManifestV3 Manifest { get; }
        public AppearanceLocation Location { get; }

        private static string Hash(byte[] content) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        private static void Write(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class PackRepository(AppearanceLocation? location) : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult(location);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StaticSurface : IPortraitSurface
    {
        public event EventHandler? StateChanged;
        public IPortraitFrame? CurrentFrame { get; private set; }
        public ExpressionSemantic Semantic { get; private set; }
        public double Scale { get; private set; }
        public int Activations { get; private set; }
        public FrameworkElement CreateView() => throw new NotSupportedException();
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            Activations++;
            CurrentFrame = new StaticFrame(PortraitLayout.Calculate(
                new PortraitSourceGeometry(303, 603, 0, 0, 1, 1, 151, 360), 0.5, new Dpi2(1, 1)));
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public void SetExpression(ExpressionSemantic semantic) { Semantic = semantic; StateChanged?.Invoke(this, EventArgs.Empty); }
        public void SetScale(double scale) { Scale = scale; StateChanged?.Invoke(this, EventArgs.Empty); }
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed record StaticFrame(PortraitGeometry Geometry) : IPortraitFrame
    {
        public void Present(FrameworkElement view) { }
        public bool IsHit(Point portraitLocalPoint) => true;
    }

    private sealed class EmptyRepository : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
