using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Windowing;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class WorkspaceWindowCoordinatorTests
{
    [Fact]
    public void Workspace_opens_in_its_own_window_and_disposes_content_on_close()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window { Width = 300, Height = 350, ShowInTaskbar = false,
                Left = -10000, Top = -10000 };
            owner.Show();
            var catalog = new Catalog();
            using var coordinator = new WorkspaceWindowCoordinator(catalog, () => owner);

            Assert.True(coordinator.Open("fixture.workspace"));
            Assert.Equal(1, catalog.CreatedCount);
            Assert.Equal(WorkspaceNavigationKind.Overview, catalog.View!.LastNavigation?.Kind);
            Assert.True(coordinator.Open("fixture.workspace"));
            Assert.Equal(1, catalog.CreatedCount);

            coordinator.Dispose();
            Assert.True(catalog.View.Disposed);
            owner.Close();
        });
    }

    private sealed class Catalog : IWorkspaceCatalog
    {
        public int CreatedCount { get; private set; }
        public View? View { get; private set; }
        public IReadOnlyList<WorkspaceDescriptor> Workspaces { get; } =
            [new("fixture.workspace", "Fixture")];
        public FrameworkElement CreateView(string workspaceId)
        {
            CreatedCount++;
            return View = new View();
        }
    }

    private sealed class View : Border, IWorkspaceSurface
    {
        public WorkspaceNavigation? LastNavigation { get; private set; }
        public bool Disposed { get; private set; }
        public void Navigate(WorkspaceNavigation navigation) => LastNavigation = navigation;
        public void Dispose() => Disposed = true;
    }
}
