using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Plugin.WebSearch;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

/// <summary>Separate explicit opt-in for the exact role/project, model route and search recipient.</summary>
internal sealed record MaterialResearchGrant(bool Enabled = false, string? RoleId = null, string? ProjectId = null,
    ModelRouteKey? ApprovedRoute = null, string SearchProvider = "duckduckgo", int DailyLimit = 6);

internal sealed class ConfiguredMaterialResearchHost(Func<MaterialResearchGrant> grant, Func<ToolScope?> current,
    Func<ModelConnectionSettings?> connection, UnderstandingService understanding, object authorityGate) : IMaterialResearchHost
{
    public MaterialResearchConfiguration? Capture()
    {
        lock (authorityGate)
        {
            try
            {
                var scope = current(); var model = connection(); var consent = grant();
                if (scope is null || model is null || !consent.Enabled || consent.DailyLimit is < 1 or > 6
                    || consent.RoleId != scope.RoleId || consent.ProjectId != scope.ProjectId
                    || consent.ApprovedRoute != ModelRouteKey.From(model) || consent.SearchProvider != "duckduckgo") return null;
                var state = understanding.State(new(scope.RoleId, scope.ProjectId));
                var policy = state.Policy;
                if (!policy.Enabled || policy.Mode == UnderstandingMode.Off || !policy.ReadSources
                    || !policy.ExternalProcessing || !policy.SaveCandidates || !policy.UseInDialogue) return null;
                var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { consent, state.Revision, state.Generation })));
                return new(scope, model, revision, consent.DailyLimit);
            }
            catch { return null; }
        }
    }
    public bool TryCommit(MaterialResearchConfiguration captured, Action action)
    {
        lock (authorityGate)
        {
            // The Understanding owner shares this fence with policy changes. The dialogue lifetime
            // additionally fences foreground priority, forgetting, maintenance and scope changes.
            return understanding.TryCommitMaterialResearch(new(captured.Scope.RoleId, captured.Scope.ProjectId), () =>
            {
                if (Capture() != captured) return false;
                action(); return true;
            });
        }
    }
}

/// <summary>Only the registered web.search provider; background work cannot enumerate or invoke other tools.</summary>
internal sealed class SingleToolMaterialResearchSource(IToolProvider search) : IMaterialResearchSource
{
    public async Task<IReadOnlyList<MaterialSearchResult>> SearchAsync(ToolScope scope, string query, CancellationToken token, string? activityId = null)
    {
        token.ThrowIfCancellationRequested();
        if (search.Descriptor.Name != "web.search" || search.Descriptor.Effect != ToolEffect.ReadOnly
            || string.IsNullOrWhiteSpace(query) || query.Length > 256)
            throw new InvalidOperationException("MATERIAL_SEARCH_REJECTED");
        var id = activityId ?? Guid.NewGuid().ToString("N");
        if (id.Length is < 1 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidOperationException("MATERIAL_CORRELATION_INVALID");
        var result = await search.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query, maxResults = 3 }))
        {
            ExecutionContext = new(id, 1, "search", id)
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new InvalidOperationException("MATERIAL_SEARCH_UNAVAILABLE");
        try
        {
            var rows = result.Payload.GetProperty("results");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 3) throw new JsonException();
            var items = new List<MaterialSearchResult>();
            foreach (var row in rows.EnumerateArray())
            {
                var title = row.GetProperty("title").GetString(); var url = row.GetProperty("url").GetString();
                var snippet = row.GetProperty("snippet").GetString();
                if (title is not { Length: > 0 and <= 1000 } || url is not { Length: > 0 and <= 2048 }
                    || snippet is not { Length: > 0 and <= 4000 }) throw new JsonException();
                items.Add(new(title.Length <= 256 ? title : title[..(char.IsHighSurrogate(title[255]) ? 255 : 256)], url, snippet));
            }
            return items;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidOperationException("MATERIAL_SEARCH_RESULT_INVALID"); }
    }
}

internal static class TopicMaterialRegistration
{
    internal static IServiceCollection AddTopicMaterialResearch(this IServiceCollection services) => services
        .AddSingleton(p => new TopicMaterialStore(p.GetRequiredService<RuntimeDatabase>(), p.GetRequiredService<TimeProvider>()))
        .AddSingleton<ITopicMaterialStore>(p => p.GetRequiredService<TopicMaterialStore>())
        .AddSingleton<ITopicMaterialEvidence>(p => p.GetRequiredService<TopicMaterialStore>())
        .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<TopicMaterialStore>())
        .AddSingleton(p => new ConversationTopicStore(p.GetRequiredService<RuntimeDatabase>(), p.GetRequiredService<TimeProvider>()))
        .AddSingleton<IShortTermTopicStore>(p => p.GetRequiredService<ConversationTopicStore>())
        .AddSingleton<IShortTermTopicSource>(p => p.GetRequiredService<ConversationTopicStore>())
        .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<ConversationTopicStore>())
        .AddSingleton<IFgoPetPlugin>(p => new TopicMaterialPlugin(p.GetRequiredService<ITopicMaterialEvidence>(),
            p.GetRequiredService<ITopicMaterialStore>().MarkUsed,
            p.GetRequiredService<IUnderstandingReader>(), p.GetRequiredService<IConversationScopeAuthority>().IsActive,
            p.GetRequiredService<IAgentQueryResolver>(), p.GetRequiredService<TimeProvider>(),
            p.GetRequiredService<IShortTermTopicSource>()))
        .AddSingleton<IMaterialResearchHost>(p => new ConfiguredMaterialResearchHost(
            () => LoadGrant(Path.Combine(p.GetRequiredService<AppPaths>().StorageRoot, "topic-material-research.json")),
            () => p.GetRequiredService<IConversationScopeAuthority>().ActiveConversation,
            () => p.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection,
            p.GetRequiredService<UnderstandingService>(), p.GetRequiredService<IDialogueSettingsStore>()))
        .AddSingleton<IMaterialResearchSource>(p => new SingleToolMaterialResearchSource(
            p.GetRequiredService<WebSearchPlugin>().Contributions.Tools.Single()))
        .AddSingleton<IMaterialResearchModel>(p => new MaterialResearchModel(p.GetRequiredService<IChatProviderResolver>(),
            p.GetRequiredService<IRequestTokenMeter>(), p.GetRequiredService<IModelContextResolver>()))
        .AddSingleton(p => new MaterialResearchActivity(p.GetRequiredService<IMaterialResearchHost>(),
            p.GetRequiredService<IUnderstandingReader>(), p.GetRequiredService<ITopicMaterialStore>(),
            p.GetRequiredService<IMaterialResearchModel>(), p.GetRequiredService<IMaterialResearchSource>(),
            p.GetRequiredService<IDialogueContextLifetime>(), p.GetRequiredService<TimeProvider>(),
            d => p.GetRequiredService<ILogger<MaterialResearchActivity>>().LogInformation(
                "Material research {At} {Correlation} {Stage} {Code}", d.At, d.Correlation, d.Stage, d.Code),
            p.GetRequiredService<IShortTermTopicSource>()))
        .AddSingleton<IFgoPetPlugin>(p => new MaterialResearchScheduler(p.GetRequiredService<MaterialResearchActivity>(),
            p.GetRequiredService<TopicMaterialStore>(), p.GetRequiredService<IDialogueContextLifetime>(),
            p.GetRequiredService<ILogger<MaterialResearchScheduler>>()));

    private static MaterialResearchGrant LoadGrant(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return new();
            return JsonSerializer.Deserialize<MaterialResearchGrant>(File.ReadAllText(path)) ?? new();
        }
        catch { return new(); }
    }
    private sealed class MaterialResearchScheduler(MaterialResearchActivity activity, TopicMaterialStore store,
        IDialogueContextLifetime lifetime, ILogger<MaterialResearchScheduler> logger)
        : IFgoPetPlugin, IApplicationReadyObserver, IDisposable
    {
        private CancellationTokenSource? _stop;
        private Task? _loop;
        private Task? _shutdown;
        private bool _closed;
        private readonly object _gate = new();
        public PluginManifest Manifest { get; } = new("firstparty.material-research", "1.0.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty;
        public ValueTask StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed || _stop is not null) throw new InvalidOperationException("MATERIAL_SCHEDULER_STOPPED");
                _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate) if (!_closed && readiness.StorageAvailable && _stop is not null && _loop is null)
            {
                var stopping = _stop.Token;
                _loop = Task.Run(() => RunAsync(stopping));
            }
            return ValueTask.CompletedTask;
        }
        private async Task RunAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    PruneExpired(token);
                    await activity.RunOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        private void PruneExpired(CancellationToken token)
        {
            try
            {
                using var lease = lifetime.TryAcquireBackground(token);
                lease?.Commit(() => { store.PruneExpired(); return true; });
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning("Material maintenance failed with {Code}", "TOPIC_MATERIAL_PRUNE_UNAVAILABLE");
            }
        }
        public ValueTask StopAsync(CancellationToken token) => new(Shutdown());
        private Task Shutdown()
        {
            lock (_gate)
            {
                _closed = true;
                return _shutdown ??= StopCoreAsync(_stop, _loop);
            }
        }
        public ValueTask DisposeAsync() => StopAsync(default);
        public void Dispose() => _ = ObserveShutdownAsync(Shutdown());
        private static async Task StopCoreAsync(CancellationTokenSource? stop, Task? loop)
        {
            if (stop is null) return;
            try
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (loop is not null) await loop.ConfigureAwait(false);
            }
            finally { stop.Dispose(); }
        }
        private static async Task ObserveShutdownAsync(Task stop)
        { try { await stop.ConfigureAwait(false); } catch { /* Async disposal still observes the same task. */ } }
    }
}
