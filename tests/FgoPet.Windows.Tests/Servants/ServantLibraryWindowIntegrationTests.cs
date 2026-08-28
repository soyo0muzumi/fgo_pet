using System.Runtime.ExceptionServices;
using System.Threading;
using FgoPet.App.Servants;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.Windows.Tests.Servants;

[Trait("Category", "WindowsIntegration")]
public sealed class ServantLibraryWindowIntegrationTests
{
    [Fact]
    public void RefreshAsync_populates_the_window_view_model_after_construction()
    {
        StaRun(async () =>
        {
            var repository = new Repository();
            var viewModel = new ServantLibraryViewModel(
                repository,
                new Installer(),
                new Controller(),
                new Settings(),
                _ => { });
            var window = new ServantLibraryWindow(viewModel);
            try
            {
                Assert.Empty(viewModel.Servants);

                await window.RefreshAsync();

                Assert.Single(viewModel.Servants);
                Assert.Equal("preview.mash", viewModel.Servants[0].PackageId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void StaRun(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Repository : IArtPackageRepository
    {
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>([new(
                "preview.mash", "mash_kyrielight", "玛修", null, "local-preview",
                [new ServantAppearance("casual", "1.0.0", "C:\\packs\\mash", null)])]);
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Installer : IPackInstaller
    {
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Controller : IPortraitController
    {
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class Settings : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => AppSettings.Defaults;
        public void Save(AppSettings settings) { }
    }
}
