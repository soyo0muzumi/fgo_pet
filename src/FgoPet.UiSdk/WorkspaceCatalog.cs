using System.Collections.Immutable;
using System.Windows;
using FgoPet.Extensibility;

namespace FgoPet.UiSdk;

/// <summary>Captures content factories, while plugins retain editing state and the shell retains window ownership.</summary>
public sealed class WorkspaceCatalog : IWorkspaceCatalog
{
    private readonly PluginCatalog _catalog;
    private readonly PluginRuntime _runtime;
    private readonly ImmutableDictionary<string, IWorkspaceViewFactory> _factories;
    public WorkspaceCatalog(PluginCatalog catalog, PluginRuntime runtime, IEnumerable<IWorkspaceViewFactory> factories)
    {
        _catalog = catalog; _runtime = runtime;
        var captured = ImmutableDictionary.CreateBuilder<string, IWorkspaceViewFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
            if (!captured.TryAdd(factory.WorkspaceId, factory)) throw new PluginValidationException("WORKSPACE_DUPLICATE_FACTORY");
        if (catalog.Workspaces.Any(entry => !captured.ContainsKey(entry.Descriptor.Id)))
            throw new PluginValidationException("WORKSPACE_MISSING_FACTORY");
        _factories = captured.ToImmutable();
    }
    public IReadOnlyList<WorkspaceDescriptor> Workspaces => _catalog.Workspaces
        .Where(entry => _runtime.IsActive(entry.PluginId)).Select(entry => entry.Descriptor).ToImmutableArray();
    public FrameworkElement CreateView(string workspaceId)
    {
        var entry = _catalog.Workspaces.FirstOrDefault(entry => entry.Descriptor.Id == workspaceId);
        if (entry is null || !_runtime.IsActive(entry.PluginId)) throw new InvalidOperationException("WORKSPACE_UNAVAILABLE");
        return _factories[workspaceId].CreateView();
    }
}

/// <summary>Hosts statically registered settings content; owning modules keep their page state.</summary>
public sealed class SettingsPageCatalog
{
    private readonly ImmutableDictionary<string, ISettingsPageViewFactory> _factories;
    public IReadOnlyList<SettingsPageDescriptor> Pages { get; }
    private readonly CancellationToken _stopping;

    public SettingsPageCatalog(IEnumerable<ISettingsPageViewFactory> factories, CancellationToken stopping = default)
    {
        var captured = ImmutableDictionary.CreateBuilder<string, ISettingsPageViewFactory>(StringComparer.Ordinal);
        var pages = ImmutableArray.CreateBuilder<SettingsPageDescriptor>();
        foreach (var factory in factories)
        {
            if (string.IsNullOrWhiteSpace(factory.SettingsPageId) || !captured.TryAdd(factory.SettingsPageId, factory))
                throw new PluginValidationException("SETTINGS_DUPLICATE_OR_INVALID_FACTORY");
            if (factory.Descriptor.Id != factory.SettingsPageId || string.IsNullOrWhiteSpace(factory.Descriptor.Title))
                throw new PluginValidationException("SETTINGS_INVALID_DESCRIPTOR");
            pages.Add(factory.Descriptor);
        }
        _factories = captured.ToImmutable();
        Pages = pages.ToImmutable();
        _stopping = stopping;
    }

    public bool Contains(string pageId) => _factories.ContainsKey(pageId);

    public FrameworkElement? CreateView(string pageId, SettingsPageRoute? route = null)
    {
        _stopping.ThrowIfCancellationRequested();
        return _factories.TryGetValue(pageId, out var factory) ? factory.CreateView(route ?? new()) : null;
    }
}
