using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.App.Theming;
using FgoPet.Extensibility;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Creates the single Web surface that presents the Settings catalog and migrated pages.</summary>
public sealed class SettingsWebRootFactory
{
    private const string DocumentUrl = "https://settings.fgopet.invalid/index.html";
    private const string GetCatalogCommand = "settings.getCatalog";
    private const string NavigateCommand = "settings.navigate";
    private static readonly Regex PageIdPattern = new(
        "\\A[A-Za-z][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ModulePattern = new(
        "\\Apages/[A-Za-z0-9_-]+\\.js\\z", RegexOptions.CultureInvariant);
    private static readonly Regex CommandPattern = new(
        "\\A[A-Za-z][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedCommands = new(
        ["ready", "theme.ack", GetCatalogCommand, NavigateCommand], StringComparer.Ordinal);

    private readonly SettingsPageCatalog _catalog;
    private readonly ImmutableDictionary<string, OwnerRegistration> _pages;
    private readonly ImmutableDictionary<string, OwnerRegistration> _commandOwners;
    private readonly string _storageRoot;
    private readonly ThemeService? _theme;

    public SettingsWebRootFactory(SettingsPageCatalog catalog, IEnumerable<ISettingsWebPage> pages,
        string storageRoot, ThemeService? theme = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(pages);
        _storageRoot = Path.GetFullPath(storageRoot ?? throw new ArgumentNullException(nameof(storageRoot)));
        _theme = theme;

        foreach (var metadata in catalog.Pages)
        {
            if (!PageIdPattern.IsMatch(metadata.Id ?? string.Empty))
                throw new PluginValidationException("SETTINGS_WEB_INVALID_CATALOG_PAGE");
        }

        var pageBuilder = ImmutableDictionary.CreateBuilder<string, OwnerRegistration>(StringComparer.Ordinal);
        var commandBuilder = ImmutableDictionary.CreateBuilder<string, OwnerRegistration>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (page is null)
                throw new PluginValidationException("SETTINGS_WEB_INVALID_OWNER");

            // Read owner-supplied metadata once. A plugin may expose mutable properties; the host
            // must keep routing and paths bound to the registration it originally reviewed.
            var pageId = page.SettingsPageId;
            var modulePath = page.ModulePath;
            var sourceCommands = page.Commands;
            if (pageId is null || !PageIdPattern.IsMatch(pageId)
                || !catalog.Contains(pageId)
                || modulePath is null || !ModulePattern.IsMatch(modulePath)
                || pageBuilder.ContainsKey(pageId))
                throw new PluginValidationException("SETTINGS_WEB_INVALID_OWNER");

            if (sourceCommands is null)
                throw new PluginValidationException("SETTINGS_WEB_INVALID_COMMANDS");
            var commandSnapshot = sourceCommands.ToArray();
            if (commandSnapshot.Length == 0)
                throw new PluginValidationException("SETTINGS_WEB_INVALID_COMMANDS");

            var seenCommands = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in commandSnapshot)
            {
                if (string.IsNullOrWhiteSpace(command) || !CommandPattern.IsMatch(command)
                    || ReservedCommands.Contains(command) || !seenCommands.Add(command))
                    throw new PluginValidationException("SETTINGS_WEB_INVALID_COMMANDS");
            }

            var registration = new OwnerRegistration(pageId, modulePath,
                [.. commandSnapshot], page);
            pageBuilder.Add(pageId, registration);
            foreach (var command in registration.Commands)
            {
                if (!commandBuilder.TryAdd(command, registration))
                    throw new PluginValidationException("SETTINGS_WEB_DUPLICATE_COMMAND");
            }
        }

        _pages = pageBuilder.ToImmutable();
        _commandOwners = commandBuilder.ToImmutable();
    }

    public WebView2SurfaceHost CreateView(string? initialPageId = null) => CreateView(initialPageId, null);

    internal WebView2SurfaceHost CreateView(string? initialPageId, Action<string>? onNavigated)
    {
        var session = CreateSession(initialPageId);
        try
        {
            var commands = new[] { GetCatalogCommand, NavigateCommand }.Concat(_commandOwners.Keys);
            var host = new WebView2SurfaceHost(new WebSurfacePolicy(DocumentUrl, commands),
                Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "root"),
                Path.Combine(_storageRoot, "WebView2", "settings", "root"), async (message, token) =>
                {
                    var result = await session.HandleCommandAsync(message, token);
                    if (result.Success && !token.IsCancellationRequested && message.Type == NavigateCommand
                        && message.Payload.TryGetProperty("pageId", out var pageId))
                        onNavigated?.Invoke(pageId.GetString()!);
                    return result;
                });
            host.Disposed += (_, _) => session.Dispose();
            if (_theme is not null)
            {
                var version = 1L;
                EventHandler changed = (_, _) => host.SetThemeVersion(
                    Interlocked.Increment(ref version), _theme.CaptureWebPalette());
                _theme.ThemeChanged += changed;
                host.Disposed += (_, _) => _theme.ThemeChanged -= changed;
                host.SetThemeVersion(version, _theme.CaptureWebPalette());
            }
            return host;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal SettingsWebRootSession CreateSession(string? initialPageId = null)
    {
        var sessionPages = ImmutableDictionary.CreateBuilder<string, OwnerRegistration>(StringComparer.Ordinal);
        var sessionCommands = ImmutableDictionary.CreateBuilder<string, OwnerRegistration>(StringComparer.Ordinal);
        foreach (var registration in _pages.Values)
        {
            var handler = registration.Owner.CreateSession();
            if (handler is null)
                throw new PluginValidationException("SETTINGS_WEB_INVALID_OWNER_SESSION");
            var scopedRegistration = registration with { Owner = handler };
            sessionPages.Add(registration.PageId, scopedRegistration);
            foreach (var command in registration.Commands)
                sessionCommands.Add(command, scopedRegistration);
        }

        return new SettingsWebRootSession(_catalog, sessionPages.ToImmutable(), sessionCommands.ToImmutable(),
            SelectInitialPage(initialPageId));
    }

    private string? SelectInitialPage(string? initialPageId)
    {
        if (!string.IsNullOrWhiteSpace(initialPageId) && _pages.ContainsKey(initialPageId))
            return initialPageId;
        return OrderedPages().FirstOrDefault(page => _pages.ContainsKey(page.Id))?.Id;
    }

    private IEnumerable<SettingsPageDescriptor> OrderedPages() => _catalog.Pages
        .OrderBy(page => page.Order)
        .ThenBy(page => page.Title, StringComparer.CurrentCulture);

    internal sealed record OwnerRegistration(string PageId, string ModulePath,
        ImmutableArray<string> Commands, ISettingsWebPage Owner);

    internal sealed class SettingsWebRootSession : IDisposable
    {
        private readonly SettingsPageCatalog _catalog;
        private readonly ImmutableDictionary<string, OwnerRegistration> _pages;
        private readonly ImmutableDictionary<string, OwnerRegistration> _commandOwners;
        private readonly object _gate = new();
        private string? _selectedPageId;
        private RouteLifetime? _route;
        private long _nextEpoch;
        private bool _navigating;
        private bool _disposed;

        internal SettingsWebRootSession(SettingsPageCatalog catalog,
            ImmutableDictionary<string, OwnerRegistration> pages,
            ImmutableDictionary<string, OwnerRegistration> commandOwners, string? selectedPageId)
        {
            _catalog = catalog;
            _pages = pages;
            _commandOwners = commandOwners;
            _selectedPageId = selectedPageId;
            if (selectedPageId is not null)
                _route = new RouteLifetime(selectedPageId, ++_nextEpoch);
        }

        internal ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (cancellationToken.IsCancellationRequested)
                return Result(false, errorCode: "WEB_SURFACE_CLOSED");
            if (string.Equals(message.Type, NavigateCommand, StringComparison.Ordinal))
                return Navigate(message.Payload, cancellationToken);

            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                if (_navigating)
                    return Result(false, errorCode: "SETTINGS_PAGE_TRANSITION");
                if (string.Equals(message.Type, GetCatalogCommand, StringComparison.Ordinal))
                    return GetCatalog();
                if (string.IsNullOrEmpty(message.Type)
                    || !_commandOwners.TryGetValue(message.Type, out var owner))
                    return Result(false, errorCode: "SETTINGS_UNKNOWN_COMMAND");
                if (!TryGetPageId(message.Payload, out var requestedPageId)
                    || !string.Equals(requestedPageId, _selectedPageId, StringComparison.Ordinal)
                    || !string.Equals(owner.PageId, _selectedPageId, StringComparison.Ordinal)
                    || _route is null || !_route.TryAcquire(cancellationToken, out var invocation))
                    return Result(false, errorCode: "SETTINGS_PAGE_MISMATCH");

                ValueTask<WebSurfaceCommandResult> pending;
                try
                {
                    // Starting the owner while the route lock is held makes acceptance atomic
                    // with navigation and disposal. Async owners retain the route token until commit.
                    if (invocation.Token.IsCancellationRequested)
                    {
                        invocation.Dispose();
                        return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                    }
                    pending = owner.Owner.HandleCommandAsync(message, invocation.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                    || invocation.Token.IsCancellationRequested)
                {
                    invocation.Dispose();
                    return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                }
                catch (Exception)
                {
                    invocation.Dispose();
                    return Result(false, errorCode: "SETTINGS_UNAVAILABLE");
                }

                return CompleteOwnerRequestAsync(pending, invocation, cancellationToken);
            }
        }

        private ValueTask<WebSurfaceCommandResult> Navigate(JsonElement payload,
            CancellationToken cancellationToken)
        {
            if (!TryGetPageId(payload, out var pageId) || !_catalog.Contains(pageId)
                || !_pages.TryGetValue(pageId, out var owner))
                return Result(false, errorCode: "SETTINGS_PAGE_UNAVAILABLE");

            RouteLifetime? oldRoute;
            string? oldPageId;
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                if (_navigating)
                    return Result(false, errorCode: "SETTINGS_PAGE_TRANSITION");
                if (string.Equals(_selectedPageId, pageId, StringComparison.Ordinal))
                    return Result(true, new { pageId, module = owner.ModulePath });

                _navigating = true;
                oldPageId = _selectedPageId;
                oldRoute = _route;
            }

            // Cancel outside the session lock so owner cancellation callbacks cannot deadlock
            // by re-entering this session. Commands are rejected while the transition is active.
            oldRoute?.Retire();

            lock (_gate)
            {
                if (_disposed)
                {
                    _navigating = false;
                    return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    _selectedPageId = oldPageId;
                    _route = oldPageId is null ? null : new RouteLifetime(oldPageId, ++_nextEpoch);
                    _navigating = false;
                    return Result(false, errorCode: "WEB_SURFACE_CLOSED");
                }

                _selectedPageId = pageId;
                _route = new RouteLifetime(pageId, ++_nextEpoch);
                _navigating = false;
                return Result(true, new { pageId, module = owner.ModulePath });
            }
        }

        private async ValueTask<WebSurfaceCommandResult> CompleteOwnerRequestAsync(
            ValueTask<WebSurfaceCommandResult> pending, RouteLifetime.Invocation invocation,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await pending.ConfigureAwait(true);
                lock (_gate)
                    return ResultIfCurrent(result, invocation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                || invocation.Token.IsCancellationRequested)
            {
                lock (_gate)
                    return ResultIfCurrent(null, invocation, cancellationToken);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    var stale = ResultIfCurrent(null, invocation, cancellationToken);
                    return stale.ErrorCode is null
                        ? new(false, ErrorCode: "SETTINGS_UNAVAILABLE")
                        : stale;
                }
            }
            finally
            {
                invocation.Dispose();
            }
        }

        private WebSurfaceCommandResult ResultIfCurrent(WebSurfaceCommandResult? result,
            RouteLifetime.Invocation invocation, CancellationToken cancellationToken)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return new(false, ErrorCode: "WEB_SURFACE_CLOSED");
            if (_navigating || !ReferenceEquals(_route, invocation.Route)
                || invocation.Route.Epoch != _nextEpoch
                || !string.Equals(_selectedPageId, invocation.Route.PageId, StringComparison.Ordinal))
                return new(false, ErrorCode: "SETTINGS_PAGE_CHANGED");
            return result ?? new(false, ErrorCode: "SETTINGS_UNAVAILABLE");
        }

        private ValueTask<WebSurfaceCommandResult> GetCatalog()
        {
            var pages = _catalog.Pages
                .OrderBy(page => page.Order)
                .ThenBy(page => page.Title, StringComparer.CurrentCulture)
                .Select(page => new
                {
                    id = page.Id,
                    title = page.Title,
                    description = page.Description,
                    group = page.Group,
                    keywords = page.Keywords,
                    order = page.Order,
                    module = _pages.TryGetValue(page.Id, out var owner) ? owner.ModulePath : null,
                    available = _pages.ContainsKey(page.Id),
                })
                .ToArray();
            return Result(true, new { pages, selectedPageId = _selectedPageId });
        }

        private static bool TryGetPageId(JsonElement payload, out string pageId)
        {
            pageId = string.Empty;
            return payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("pageId", out var pageIdElement)
                && pageIdElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(pageId = pageIdElement.GetString() ?? string.Empty);
        }

        private static ValueTask<WebSurfaceCommandResult> Result(bool success, object? payload = null,
            string? errorCode = null) => ValueTask.FromResult(new WebSurfaceCommandResult(success, payload, errorCode));

        public void Dispose()
        {
            RouteLifetime? route;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _navigating = false;
                _selectedPageId = null;
                route = _route;
                _route = null;
            }
            route?.Retire();
        }
    }

    private sealed class RouteLifetime(string pageId, long epoch)
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private int _active;
        private bool _retired;
        private bool _cancelCompleted;
        private bool _sourceDisposed;

        public string PageId { get; } = pageId;
        public long Epoch { get; } = epoch;

        public bool TryAcquire(CancellationToken hostToken, out Invocation invocation)
        {
            lock (_gate)
            {
                if (_retired || _sourceDisposed)
                {
                    invocation = null!;
                    return false;
                }
                _active++;
                try
                {
                    var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_source.Token, hostToken);
                    invocation = new Invocation(this, linkedToken);
                    return true;
                }
                catch
                {
                    _active--;
                    throw;
                }
            }
        }

        public void Retire()
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;
            }

            try
            {
                _source.Cancel();
            }
            catch (AggregateException)
            {
                // The cancellation state is already set. A faulty owner callback cannot keep the
                // old route alive or prevent session shutdown.
            }
            finally
            {
                lock (_gate)
                {
                    _cancelCompleted = true;
                    DisposeSourceIfIdle();
                }
            }
        }

        private void Release()
        {
            lock (_gate)
            {
                _active--;
                DisposeSourceIfIdle();
            }
        }

        private void DisposeSourceIfIdle()
        {
            if (_retired && _cancelCompleted && _active == 0 && !_sourceDisposed)
            {
                _source.Dispose();
                _sourceDisposed = true;
            }
        }

        internal sealed class Invocation(RouteLifetime route, CancellationTokenSource linkedSource) : IDisposable
        {
            private RouteLifetime? _route = route;
            private CancellationTokenSource? _linkedSource = linkedSource;
            public RouteLifetime Route { get; } = route;
            public CancellationToken Token => _linkedSource?.Token ?? default;
            public void Dispose()
            {
                Interlocked.Exchange(ref _linkedSource, null)?.Dispose();
                Interlocked.Exchange(ref _route, null)?.Release();
            }
        }
    }
}
