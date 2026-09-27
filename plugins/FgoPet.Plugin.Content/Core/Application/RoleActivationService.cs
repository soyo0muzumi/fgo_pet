using FgoPet.App.Runtime;
using FgoPet.Character.Settings;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

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
public interface IRoleActivationService
{
    Task<RoleActivationResult> ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken);

    Task<RoleActivationResult> RestoreAsync(CancellationToken cancellationToken);
}

public sealed class RoleActivationService : IRoleActivationService
{
    private readonly IArtPackageRepository _repository;
    private readonly IPortraitController _portrait;
    private readonly ICharacterSettingsStore _settings;
    private readonly AppRuntime _runtime;
    private readonly SemaphoreSlim _activationGate = new(1, 1);

    public RoleActivationService(
        IArtPackageRepository repository,
        IPortraitController portrait,
        ICharacterSettingsStore settings,
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

        // Keep rendered appearance, current identity and persisted selection in one
        // ordered transaction. Cancelled waiters never enter the renderer.
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ActivateCoreAsync(selection, cancellationToken).ConfigureAwait(false); }
        finally { _activationGate.Release(); }
    }

    public async Task ActivatePortraitAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        var result = await ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new PackFailureException(new(PackErrorCode.AssetMissing, result.Error ?? "角色外观激活失败。"));
    }

    private async Task<RoleActivationResult> ActivateCoreAsync(
        PortraitSelection selection, CancellationToken cancellationToken)
    {

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
            await _portrait.ActivateAsync(selection with { PackageVersion = location.Identity.PackageVersion }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RoleActivationResult.Failed(RoleActivationFailure.ActivationFailed, "角色外观激活失败。");
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
        var resolved = await _repository.ResolveStartupSelectionAsync(saved, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return saved is null
                ? RoleActivationResult.Failed(RoleActivationFailure.NoSelection, "尚未发现可用角色包，请从本地文件恢复。")
                : RoleActivationResult.Failed(RoleActivationFailure.MissingPackage, "已保存的角色包不可用。");
        }

        var selection = new PortraitSelection(
            resolved.Identity.PackageId,
            resolved.AppearanceId,
            resolved.Identity.PackageVersion);
        return await ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
    }
}
