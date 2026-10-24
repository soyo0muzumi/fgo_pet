using System.IO;
using System.Text.Json;
using FgoPet.App.Companion;
using FgoPet.App.Dialogue;
using FgoPet.App.Focus;
using FgoPet.App.Runtime;
using FgoPet.App.Services;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using FgoPet.Platform.WindowText;
using FgoPet.Platform.Windows.WindowText;
using FgoPet.Plugin.DesktopContext;
using FgoPet.Plugin.Reminders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

internal static class CompanionAwarenessRegistration
{
    internal static IServiceCollection AddCompanionAwareness(this IServiceCollection services) => services
        .AddSingleton(provider => new CompanionAwarenessStore(Path.Combine(
            provider.GetRequiredService<AppPaths>().StorageRoot, "companion-awareness.sqlite")))
        .AddSingleton(provider => new CompanionAwarenessHost(
            provider.GetRequiredService<CompanionAwarenessStore>(),
            () => provider.GetRequiredService<IConversationScopeAuthority>().ActiveConversation,
            () => provider.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection,
            (scope, token) => ReadActivityAsync(provider, scope, token),
            token => ReadApplications(provider.GetRequiredService<TimeProvider>(), token),
            // Session-only ambient consent owns its own candidate map and reader lifetime.
            new WindowsWindowTextReader(provider.GetRequiredService<IWindowTextDiagnostics>()),
            provider.GetRequiredService<IDialogueContextLifetime>(), provider.GetRequiredService<TimeProvider>(),
            authorityGate: provider.GetRequiredService<IDialogueSettingsStore>()))
        .AddSingleton<ICompanionAwarenessControls>(provider => provider.GetRequiredService<CompanionAwarenessHost>())
        .AddSingleton<IDerivedConversationData>(provider => provider.GetRequiredService<CompanionAwarenessHost>())
        .AddSingleton(provider =>
        {
            var host = provider.GetRequiredService<CompanionAwarenessHost>();
            var coordinator = new CompanionActivityCoordinator(host,
                new CompanionActivityModel(provider.GetRequiredService<IChatProviderResolver>(),
                    provider.GetRequiredService<IRequestTokenMeter>(), provider.GetRequiredService<IModelContextResolver>()),
                host, provider.GetRequiredService<IDialogueContextLifetime>(), provider.GetRequiredService<TimeProvider>(),
                diagnostic =>
                {
                    host.Record(diagnostic);
                    provider.GetRequiredService<ILogger<CompanionActivityCoordinator>>().LogInformation(
                        "Companion activity {Timestamp} {ActivityId} {Stage} {ErrorCode}", diagnostic.Timestamp,
                        diagnostic.ActivityId, diagnostic.Stage, diagnostic.ErrorCode);
                });
            host.Attach(coordinator);
            return coordinator;
        })
        .AddSingleton<IFgoPetPlugin>(provider => new CompanionAwarenessPlugin(provider));

    private static async Task<IReadOnlyList<CompanionObservation>> ReadActivityAsync(IServiceProvider provider,
        ToolScope scope, CancellationToken token)
    {
        var result = new List<CompanionObservation>();
        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        result.Add(CompanionAwarenessHost.Observe("time.period", CompanionAwarenessHost.SerializeData(new
        {
            localDate = local.ToString("yyyy-MM-dd"), timeZone = TimeZoneInfo.Local.Id,
            period = local.Hour switch { < 6 => "night", < 12 => "morning", < 18 => "afternoon", _ => "evening" }
        }), now, TimeSpan.FromMinutes(2)));
        if (provider.GetService<IFocusSessionService>() is { } focus && provider.GetService<IFocusNativeDispatcher>() is { } dispatcher)
        {
            try
            {
                var session = await dispatcher.InvokeAsync(() => focus.Current, token).ConfigureAwait(false);
                if (session.ServantId == scope.RoleId)
                    result.Add(CompanionAwarenessHost.Observe("activity.focus", CompanionAwarenessHost.SerializeData(new
                    {
                        status = session.Status.ToString(), phase = session.Phase.ToString(),
                        session.CurrentCycle, session.TotalCycles, session.IsCurrent, session.SessionId,
                        meaning = "Timer state only; a completed interval does not establish task completion."
                    }), now, TimeSpan.FromMinutes(2)));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        if (provider.GetService<ITodoRepository>() is { } todos)
        {
            try
            {
                var items = todos.List();
                result.Add(CompanionAwarenessHost.Observe("activity.todos", CompanionAwarenessHost.SerializeData(new
                {
                    pending = items.Count(item => item.Status == TodoStatus.Planned),
                    active = items.Count(item => item.Status == TodoStatus.Active),
                    recent = items.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).Take(3)
                        .Select(item => new { title = CompanionAwarenessHost.Limit(item.Title, 60),
                            status = item.Status.ToString(), item.DueAt }),
                    scope = "Shared local task book, not isolated by companion or conversation"
                }), now, TimeSpan.FromMinutes(2)));
            }
            catch { }
        }
        if (provider.GetService<ReminderService>() is { } reminders)
        {
            try
            {
                var items = reminders.List(scope, limit: 3);
                result.Add(CompanionAwarenessHost.Observe("activity.reminders", CompanionAwarenessHost.SerializeData(new
                {
                    items = items.Take(3).Select(item => new { message = CompanionAwarenessHost.Limit(item.Message, 60),
                        item.DueAtUtc, status = item.Status.ToString() }),
                    scope = "Current conversation only", truncated = items.Count > 3,
                    meaning = "Submitted does not prove the user saw the notification."
                }), now, TimeSpan.FromMinutes(2)));
            }
            catch { }
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static CompanionObservation ReadApplications(TimeProvider clock, CancellationToken token)
    {
        var snapshot = WindowsApplicationDiscovery.Read(token);
        var content = CompanionAwarenessHost.SerializeData(new
        {
            applications = snapshot.Applications.Take(8).Select(item => new
                { name = CompanionAwarenessHost.Limit(item.Name, 40), item.WindowCount, item.IsForeground }),
            truncated = snapshot.Truncated || snapshot.Applications.Count > 8,
            snapshot.ForegroundObserved,
            meaning = "Application presence is not evidence of user intent or task completion."
        });
        return CompanionAwarenessHost.Observe("desktop.applications", content, clock.GetUtcNow(), TimeSpan.FromMinutes(2));
    }

    private sealed class CompanionAwarenessPlugin(IServiceProvider services) : IFgoPetPlugin, IApplicationReadyObserver
    {
        private CancellationToken _processStopping;
        private CompanionAwarenessHost? _host;
        private CompanionSignalPipeline? _signals;
        private AppRuntime? _runtime;
        private TodoApplicationService? _todos;
        private ModelConnectionViewModel? _modelSettings;
        public PluginManifest Manifest { get; } = new("companion-awareness", "1.0.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty;
        public ValueTask StartAsync(CancellationToken token) { _processStopping = token; return ValueTask.CompletedTask; }
        public ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!readiness.StorageAvailable || _host is not null) return ValueTask.CompletedTask;
            _host = services.GetRequiredService<CompanionAwarenessHost>();
            _ = services.GetRequiredService<CompanionActivityCoordinator>();
            _signals = services.GetRequiredService<CompanionSignalPipeline>();
            _runtime = services.GetRequiredService<AppRuntime>();
            _todos = services.GetService<TodoApplicationService>();
            _modelSettings = services.GetRequiredService<ModelConnectionViewModel>();
            _signals.Signal += OnSignal;
            _runtime.ActiveRoleChanged += OnRoleChanged;
            if (_todos is not null) _todos.Changed += OnChanged;
            _modelSettings.ConnectionSaved += OnModelChanged;
            _host.Start(_processStopping);
            return ValueTask.CompletedTask;
        }
        private void OnSignal(CompanionSignal signal) => _host?.ContextChanged();
        private void OnRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args) => _host?.ContextChanged();
        private void OnChanged() => _host?.ContextChanged();
        private void OnModelChanged(FgoPet.Core.Settings.ModelConnectionSettings _) => _host?.ContextChanged();
        public async ValueTask StopAsync(CancellationToken token)
        {
            if (_signals is not null) _signals.Signal -= OnSignal;
            if (_runtime is not null) _runtime.ActiveRoleChanged -= OnRoleChanged;
            if (_todos is not null) _todos.Changed -= OnChanged;
            if (_modelSettings is not null) _modelSettings.ConnectionSaved -= OnModelChanged;
            if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => StopAsync(default);
    }
}
