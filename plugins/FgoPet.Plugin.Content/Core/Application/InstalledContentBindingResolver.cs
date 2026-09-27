using FgoPet.Character.Settings;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Dialogue;

public sealed class InstalledContentBindingResolver : IConversationContentResolver
{
    private readonly IArtPackageRepository _repository;
    private readonly ICharacterSettingsStore _settings;

    public InstalledContentBindingResolver(IArtPackageRepository repository, ICharacterSettingsStore settings)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
    {
        var catalog = await _repository.ScanAsync(cancellationToken);
        var candidates = catalog.Packs
            .Where(pack => string.Equals(pack.ServantId, servantId, StringComparison.Ordinal))
            .ToArray();
        var selection = _settings.Load().Selection;
        var selected = candidates.FirstOrDefault(pack =>
                selection is not null
                && pack.PackageId == selection.PackageId
                && (selection.PackageVersion is null || pack.PackageVersion == selection.PackageVersion)
                && pack.Appearances.Any(appearance => appearance.AppearanceId == selection.AppearanceId))
            ?? candidates.OrderByDescending(pack => pack.Version).FirstOrDefault();
        if (selected is null)
        {
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "当前从者没有可用角色包。");
        }

        var appearanceId = selected.Appearances.FirstOrDefault(appearance =>
                selection is not null && appearance.AppearanceId == selection.AppearanceId)?.AppearanceId
            ?? selected.Appearances.First().AppearanceId;
        return ContentBindingResolver.Resolve(selected.PackRoot, servantId, appearanceId);
    }
}
