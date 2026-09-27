using System.Windows;
using System.ComponentModel;

namespace FgoPet.UiSdk;

public enum WorkspaceNavigationKind { Overview, Activate, NewItem, ExistingItem }
public sealed record WorkspaceNavigation(WorkspaceNavigationKind Kind, string? ItemId = null);
public interface IWorkspaceSurface : IDisposable
{
    void Navigate(WorkspaceNavigation navigation);
}

public interface IWorkspaceCatalog
{
    IReadOnlyList<FgoPet.Extensibility.WorkspaceDescriptor> Workspaces { get; }
    FrameworkElement CreateView(string workspaceId);
}

/// <summary>Feature-owned content factory. The shell owns windows, placement and navigation.</summary>
public interface IWorkspaceViewFactory
{
    string WorkspaceId { get; }
    FrameworkElement CreateView();
}

public interface ISettingsPageViewFactory
{
    string SettingsPageId { get; }
    FgoPet.Extensibility.SettingsPageDescriptor Descriptor => new(SettingsPageId, SettingsPageId);
    FrameworkElement CreateView();
    FrameworkElement CreateView(SettingsPageRoute route) => CreateView();
}

/// <summary>The owner supplies compact content and its activity; the shell owns container state.</summary>
public interface ICompactSurface : INotifyPropertyChanged
{
    bool IsActive { get; }
    bool BlocksAutoCollapse { get; }
    event Action? Interaction;
    FrameworkElement CreateView();
}

public interface ICompactSurfaceView : IDisposable
{
    void SetExpanded(bool expanded);
}

/// <summary>Renderer-owned content and hit testing; the shell only consumes logical geometry.</summary>
public interface IPortraitSurface : FgoPet.Core.Portraits.IPortraitController
{
    event EventHandler? StateChanged;
    IPortraitFrame? CurrentFrame { get; }
    FrameworkElement CreateView();
}

/// <summary>Captures one immutable published renderer state for presentation and hit testing.</summary>
public interface IPortraitFrame
{
    FgoPet.Core.Geometry.PortraitGeometry Geometry { get; }
    void Present(FrameworkElement view);
    bool IsHit(Point portraitLocalPoint);
}

public interface ISettingsPageNavigator
{
    void Navigate(string settingsPageId);
}

public sealed record SettingsPageRoute(string? ItemId = null, string? DisplayName = null);

/// <summary>Content declares whether it supplies its own scroll and heading layout.</summary>
public interface ISettingsPageSurface
{
    bool OwnsScrolling { get; }
}

/// <summary>Captures an owner-supplied constructor without creating a view during registration.</summary>
public sealed class SettingsPageViewFactory(string pageId, Func<SettingsPageRoute, FrameworkElement> create,
    string? title = null, string? description = null)
    : ISettingsPageViewFactory
{
    public string SettingsPageId { get; } = pageId;
    public FgoPet.Extensibility.SettingsPageDescriptor Descriptor { get; } = new(pageId, title ?? pageId)
    {
        Description = description ?? string.Empty
    };
    public FrameworkElement CreateView() => create(new());
    public FrameworkElement CreateView(SettingsPageRoute route) => create(route);
}
