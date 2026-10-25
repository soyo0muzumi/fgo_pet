using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Reflection;
using System.Windows.Threading;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Companion;
using Microsoft.Extensions.DependencyInjection;
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
    [Fact]
    public void Chat_projection_tracks_the_current_turn_and_rejects_stale_or_disposed_work()
    {
        StaTest.Run(() =>
        {
            using var services = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
            var conversations = services.GetRequiredService<ConversationOrchestrator>();
            var identity = new AppRuntime();
            using var presentation = new CompanionPresentation(identity);
            var fallback = new StaticSurface();
            using var portrait = new Live2DPortraitController(fallback, new EmptyRepository(),
                Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath());
            portrait.ActivateAsync(new PortraitSelection("pkg", "casual", "1.0.0"), default).GetAwaiter().GetResult();
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1.0.0", "mash"));
            using var plugin = new Live2DPresentationPlugin(presentation, () => conversations, portrait);
            plugin.StartAsync(default).GetAwaiter().GetResult();
            // Inject events through the real subscribed delegate, without network or persistence.
            var updated = typeof(ConversationOrchestrator).GetField("Updated", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sequence = 0;
            var request = "";
            void Emit(ConversationUpdateType type, string turn = "turn", ExpressionSemantic? expression = null,
                string? text = null, string? reasoning = null, ConversationRequestStage? stage = null)
            {
                if (type == ConversationUpdateType.UserMessagePersisted) request = "request-" + ++sequence;
                ((Action<ConversationUpdate>)updated.GetValue(conversations)!).Invoke(
                    new(type, turn, TextDelta: text, ReasoningDelta: reasoning,
                        RequestStage: stage, ServantId: "mash", Expression: expression, RequestId: request));
            }
            string Phase() => (string)typeof(Live2DPortraitController)
                .GetField("_dialoguePhase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(portrait)!;
            bool Thinking() => Phase() == "waiting";
            void Drain()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }

            Emit(ConversationUpdateType.UserMessagePersisted); Drain(); Assert.True(Thinking());
            Emit(ConversationUpdateType.AssistantCompleted, "other"); Drain(); Assert.True(Thinking());
            Emit(ConversationUpdateType.AssistantDelta, reasoning: "synthetic reasoning");
            Drain(); Assert.True(Thinking());
            Emit(ConversationUpdateType.AssistantDelta, text: ""); Drain(); Assert.True(Thinking());
            Emit(ConversationUpdateType.AssistantDelta, text: "synthetic answer"); Drain(); Assert.Equal("replying", Phase());
            Emit(ConversationUpdateType.AssistantCompleted, expression: ExpressionSemantic.Shy);
            Drain(); Assert.Equal("idle", Phase()); Assert.Equal(ExpressionSemantic.Shy, fallback.Semantic);

            foreach (var ending in new[] { ConversationUpdateType.Cancelled, ConversationUpdateType.Failed })
            {
                Emit(ConversationUpdateType.UserMessagePersisted); Drain(); Assert.True(Thinking());
                Emit(ending); Drain(); Assert.Equal("idle", Phase());
                Emit(ConversationUpdateType.AssistantDelta, text: "late answer");
                Emit(ConversationUpdateType.AssistantCompleted, expression: ExpressionSemantic.Shy);
                Drain(); Assert.Equal("idle", Phase()); Assert.Equal(ExpressionSemantic.Neutral, fallback.Semantic);
            }
            Emit(ConversationUpdateType.UserMessagePersisted); Drain(); Assert.True(Thinking());
            Emit(ConversationUpdateType.RequestStage, stage: ConversationRequestStage.StreamingAnswer);
            Drain(); Assert.Equal("replying", Phase());
            Emit(ConversationUpdateType.Cancelled); Drain();

            // A newer admitted request invalidates an old completion already queued on the dispatcher.
            Emit(ConversationUpdateType.UserMessagePersisted); Drain();
            fallback.Expressions.Clear();
            Emit(ConversationUpdateType.AssistantCompleted, expression: ExpressionSemantic.Shy);
            Emit(ConversationUpdateType.UserMessagePersisted);
            Drain(); Assert.True(Thinking()); Assert.DoesNotContain(ExpressionSemantic.Shy, fallback.Expressions);
            Emit(ConversationUpdateType.Cancelled); Drain(); Assert.Equal("idle", Phase());

            Emit(ConversationUpdateType.UserMessagePersisted);
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1.0.0", "mash"));
            Drain(); Assert.False(Thinking());
            Emit(ConversationUpdateType.UserMessagePersisted);
            plugin.Dispose(); Drain(); Assert.False(Thinking());
        });
    }

    [Theory]
    [InlineData(false, ConversationUpdateType.AssistantCompleted)]
    [InlineData(false, ConversationUpdateType.Cancelled)]
    [InlineData(false, ConversationUpdateType.Failed)]
    [InlineData(true, ConversationUpdateType.AssistantCompleted)]
    [InlineData(true, ConversationUpdateType.Cancelled)]
    [InlineData(true, ConversationUpdateType.Failed)]
    public void Late_previous_request_cannot_change_new_feedback(bool queuedBeforeB, ConversationUpdateType ending)
    {
        StaTest.Run(() =>
        {
            using var services = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
            var conversations = services.GetRequiredService<ConversationOrchestrator>();
            var identity = new AppRuntime();
            using var presentation = new CompanionPresentation(identity);
            var fallback = new StaticSurface();
            using var portrait = new Live2DPortraitController(fallback, new EmptyRepository(),
                Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath());
            portrait.ActivateAsync(new PortraitSelection("pkg", "casual", "1.0.0"), default).GetAwaiter().GetResult();
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1.0.0", "mash"));
            using var plugin = new Live2DPresentationPlugin(presentation, () => conversations, portrait);
            plugin.StartAsync(default).GetAwaiter().GetResult();
            var observer = (Action<ConversationUpdate>)typeof(ConversationOrchestrator)
                .GetField("Updated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(conversations)!;
            void Emit(ConversationUpdateType type, string? request, string? text = null,
                string turn = "same-conversation", string? reasoning = null, ExpressionSemantic? expression = null) =>
                observer(new(type, turn, TextDelta: text, ReasoningDelta: reasoning,
                    ServantId: "mash", Expression: expression, RequestId: request));
            void Drain()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
            string Phase() => (string)typeof(Live2DPortraitController)
                .GetField("_dialoguePhase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(portrait)!;

            Emit(ConversationUpdateType.UserMessagePersisted, "A"); Drain(); Assert.Equal("waiting", Phase());
            if (queuedBeforeB)
            {
                Emit(ConversationUpdateType.AssistantDelta, "A", "queued old answer");
                Emit(ConversationUpdateType.AssistantCompleted, "A", expression: ExpressionSemantic.Happy);
                Emit(ConversationUpdateType.Cancelled, "A");
            }
            else { Emit(ConversationUpdateType.Cancelled, "A"); Drain(); Assert.Equal("idle", Phase()); }
            Emit(ConversationUpdateType.UserMessagePersisted, "B"); Drain(); Assert.Equal("waiting", Phase());
            foreach (var stale in new[] { ConversationUpdateType.AssistantDelta,
                ConversationUpdateType.AssistantCompleted, ConversationUpdateType.Cancelled, ConversationUpdateType.Failed })
            {
                Emit(stale, "A", "late old answer", expression: ExpressionSemantic.Happy);
                Drain(); Assert.Equal("waiting", Phase()); Assert.Equal(ExpressionSemantic.Neutral, fallback.Semantic);
            }
            Emit(ConversationUpdateType.AssistantDelta, null, "missing identity");
            Emit(ConversationUpdateType.AssistantDelta, "B", " ");
            Emit(ConversationUpdateType.AssistantDelta, "B", reasoning: "synthetic reasoning");
            Drain(); Assert.Equal("waiting", Phase());
            Emit(ConversationUpdateType.AssistantDelta, "B", "current answer"); Drain(); Assert.Equal("replying", Phase());
            Emit(ConversationUpdateType.Cancelled, "A"); Drain(); Assert.Equal("replying", Phase());
            Emit(ending, "B"); Drain(); Assert.Equal("idle", Phase());

            Emit(ConversationUpdateType.UserMessagePersisted, "C", turn: "new-conversation"); Drain();
            Emit(ConversationUpdateType.Cancelled, "B"); Drain(); Assert.Equal("waiting", Phase());
            identity.SetActiveRole(new ActiveRoleState("pkg", "casual", "1.0.0", "mash"));
            Drain(); Assert.Equal("idle", Phase());
            Emit(ConversationUpdateType.AssistantDelta, "C", "old role answer", turn: "new-conversation");
            Drain(); Assert.Equal("idle", Phase());
            Emit(ConversationUpdateType.UserMessagePersisted, "D");
            plugin.Dispose(); Drain(); Assert.Equal("idle", Phase());
            Emit(ConversationUpdateType.UserMessagePersisted, "E"); Drain(); Assert.Equal("idle", Phase());
        });
    }

    [Theory]
    [InlineData(1.0, 266)]
    [InlineData(1.5, 398)]
    [InlineData(2.0, 531)]
    public void Mash_viewport_reserves_headroom_and_side_space_at_the_existing_character_scale(double dpiScale, int expectedDeviceWidth)
    {
        var staticGeometry = PortraitLayout.Calculate(
            new PortraitSourceGeometry(303, 603, 13, 0, 256, 240, 151, 360),
            0.5, new Dpi2(dpiScale, dpiScale));

        var liveGeometry = Live2DPortraitController.MashGeometry(staticGeometry);

        Assert.Equal(staticGeometry.LogicalSize.Height + 36, liveGeometry.LogicalSize.Height);
        Assert.Equal(265.5, liveGeometry.LogicalSize.Width);
        Assert.Equal(36, liveGeometry.OverlayLogicalRect.Y);
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
        public List<ExpressionSemantic> Expressions { get; } = [];
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
        public void SetExpression(ExpressionSemantic semantic) { Expressions.Add(semantic); Semantic = semantic; StateChanged?.Invoke(this, EventArgs.Empty); }
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
