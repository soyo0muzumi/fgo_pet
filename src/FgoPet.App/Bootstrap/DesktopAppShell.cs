using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;

namespace FgoPet.App.Bootstrap;

public interface IDesktopAppUi
{
    void InitializeTray();
    void ShowLibrary(string? offeredPackPath = null);
    void ShowPortrait();
}

/// <summary>Connects persisted selection and installed packs to the desktop UI.</summary>
public sealed class DesktopAppShell : IAppShell
{
    private readonly IArtPackageRepository _repository;
    private readonly IPortraitController _controller;
    private readonly IAppSettingsStore _settings;
    private readonly IDesktopAppUi _ui;

    public DesktopAppShell(
        IArtPackageRepository repository,
        IPortraitController controller,
        IAppSettingsStore settings,
        IDesktopAppUi ui)
    {
        _repository = repository;
        _controller = controller;
        _settings = settings;
        _ui = ui;
    }

    public async Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _ui.InitializeTray();
        var offeredPack = arguments.FirstOrDefault(path =>
            path.EndsWith(".fgopetpack", StringComparison.OrdinalIgnoreCase));
        if (offeredPack is not null)
        {
            _ui.ShowLibrary(offeredPack);
            return;
        }

        var requested = _settings.Load().Selection;
        var location = await _repository.ResolveStartupSelectionAsync(requested, cancellationToken);
        if (location is null)
        {
            _ui.ShowLibrary();
            return;
        }

        var resolved = new PortraitSelection(
            location.Identity.PackageId,
            location.AppearanceId,
            location.Identity.PackageVersion);
        await _controller.ActivateAsync(resolved, cancellationToken);
        _ui.ShowPortrait();
    }
}
