using FgoPet.App.Runtime;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;

namespace FgoPet.App.Servants;

public enum RoleActivationFailure
{
    None,
    NoSelection,
    MissingPackage,
    ActivationFailed,
}

public sealed record RoleActivationResult(
    bool Succeeded,
    ActiveRoleState? ActiveRole,
    RoleActivationFailure Failure,
    string? Error)
{
    public static RoleActivationResult Success(ActiveRoleState role) => new(true, role, RoleActivationFailure.None, null);

    public static RoleActivationResult Failed(RoleActivationFailure failure, string? error = null) =>
        new(false, null, failure, error);
}

/// <summary>Runs the complete role activation use case for every entry point.</summary>
public sealed class RoleActivationService
{
    private readonly IArtPackageRepository _repository;
    private readonly IPortraitController _portrait;
    private readonly IAppSettingsStore _settings;
    private readonly AppRuntime _runtime;

    public RoleActivationService(
        IArtPackageRepository repository,
        IPortraitController portrait,
        IAppSettingsStore settings,
        AppRuntime runtime)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _portrait = portrait ?? throw new ArgumentNullException(nameof(portrait));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<RoleActivationResult> ActivateAsync(
        PortraitSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var location = await _repository.GetAppearanceAsync(selection, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.MissingPackage, "角色包或外观不存在。");
        }

        var servants = await _repository.ListServantsAsync(cancellationToken).ConfigureAwait(false);
        var servant = servants.FirstOrDefault(candidate =>
            candidate.PackageId == selection.PackageId
            && candidate.Appearances.Any(appearance =>
                appearance.AppearanceId == selection.AppearanceId
                && appearance.PackageVersion == location.Identity.PackageVersion));
        if (servant is null)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.MissingPackage, "角色包身份信息不存在。");
        }

        try
        {
            await _portrait.ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.ActivationFailed, error.Message);
        }

        var role = new ActiveRoleState(
            selection.PackageId,
            selection.AppearanceId,
            location.Identity.PackageVersion,
            servant.ServantId);
        _runtime.SetActiveRole(role);
        _settings.Save(_settings.Load() with { Selection = selection with { PackageVersion = location.Identity.PackageVersion } });
        return RoleActivationResult.Success(role);
    }

    public async Task<RoleActivationResult> RestoreAsync(CancellationToken cancellationToken)
    {
        var saved = _settings.Load().Selection;
        if (saved is null)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.NoSelection, "尚未选择角色包。");
        }

        var resolved = await _repository.ResolveStartupSelectionAsync(saved, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.MissingPackage, "已保存的角色包不可用。");
        }

        var selection = new PortraitSelection(
            resolved.Identity.PackageId,
            resolved.AppearanceId,
            resolved.Identity.PackageVersion);
        return await ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
    }
}
