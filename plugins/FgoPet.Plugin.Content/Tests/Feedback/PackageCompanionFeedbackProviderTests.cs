using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Plugin.Content;
using Xunit;

namespace FgoPet.Plugin.Content.Tests;

public sealed class PackageCompanionFeedbackProviderTests
{
    [Fact]
    public async Task Cache_is_scoped_to_exact_package_version_and_appearance_even_for_the_same_role()
    {
        var repository = new Repository();
        var provider = new PackageCompanionFeedbackProvider(repository);
        var signal = new CompanionSignal(CompanionSignalKind.ActivityStarted, "role", "activity", DateTimeOffset.UnixEpoch, 1, CompanionActivityPhase.Work, 0);
        var first = new CompanionContentScope("a", "1.0.0", "casual", "role");
        var second = first with { PackageVersion = "2.0.0" };
        Assert.NotNull(await provider.GetFeedbackAsync(signal, first, default));
        await provider.GetFeedbackAsync(signal, first, default);
        await provider.GetFeedbackAsync(signal, second, default);
        await provider.GetFeedbackAsync(signal, first, default);
        Assert.Equal(new[] { new PortraitSelection("a", "casual", "1.0.0"), new("a", "casual", "2.0.0"), new("a", "casual", "1.0.0") }, repository.Requests);
    }

    [Fact]
    public async Task Failed_scope_load_does_not_leave_previous_scope_cached_without_its_bundle()
    {
        var repository = new Repository
        {
            AppearanceRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", "dialogue-valid"),
            FailedVersion = "2.0.0"
        };
        var provider = new PackageCompanionFeedbackProvider(repository);
        var signal = new CompanionSignal(CompanionSignalKind.ActivityStarted, "role", "activity", DateTimeOffset.UnixEpoch, 1, CompanionActivityPhase.Work, 0);
        var first = new CompanionContentScope("a", "1.0.0", "casual", "role");
        var failed = first with { PackageVersion = "2.0.0" };

        var initial = await provider.GetFeedbackAsync(signal, first, default);
        Assert.NotNull(initial);
        Assert.Contains(initial!.Text, new[] { "开始一段专注吧。", "计时开始，剩下的交给我。" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetFeedbackAsync(signal, failed, default));

        var restored = await provider.GetFeedbackAsync(signal, first, default);

        Assert.NotNull(restored);
        Assert.Contains(restored!.Text, new[] { "开始一段专注吧。", "计时开始，剩下的交给我。" });
        Assert.Equal(new[] { new PortraitSelection("a", "casual", "1.0.0"), new("a", "casual", "2.0.0"), new("a", "casual", "1.0.0") }, repository.Requests);
    }

    private sealed class Repository : IArtPackageRepository
    {
        public List<PortraitSelection> Requests { get; } = [];
        public string? AppearanceRoot { get; init; }
        public string? FailedVersion { get; init; }
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            Requests.Add(selection);
            if (selection.PackageVersion == FailedVersion)
                return Task.FromException<AppearanceLocation?>(new InvalidOperationException("The scope could not be loaded."));
            return Task.FromResult<AppearanceLocation?>(AppearanceRoot is null
                ? null
                : new AppearanceLocation(new PackIdentity(selection.PackageId, selection.PackageVersion!), selection.AppearanceId, AppearanceRoot));
        }
        public Task<PackCatalog> ScanAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? selection, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string packageId, string version, CancellationToken token) => throw new NotSupportedException();
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken token) => throw new NotSupportedException();
    }
}
