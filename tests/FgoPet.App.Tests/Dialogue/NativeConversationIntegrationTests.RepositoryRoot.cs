using System.IO;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public void Repository_root_explicit_checkout_supports_external_test_output()
    {
        var root = P2RepositoryRoot();
        var result = ResolveTestRepositoryRoot(Path.GetTempPath(), root);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(root), result.FullName);
    }

    [Fact]
    public void Repository_root_invalid_explicit_checkout_does_not_fall_back()
    {
        var root = P2RepositoryRoot();
        Assert.Null(ResolveTestRepositoryRoot(root, Path.Combine(root, "tests")));
        Assert.Null(ResolveTestRepositoryRoot(root, "tests"));
        Assert.Null(ResolveTestRepositoryRoot(root, " "));
    }

    [Fact]
    public void Repository_root_default_keeps_ancestor_lookup()
    {
        var root = P2RepositoryRoot();
        var result = ResolveTestRepositoryRoot(Path.Combine(root, "tests", "FgoPet.App.Tests"), null);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(root), result.FullName);
    }

    [Fact]
    public void Repository_root_unrelated_output_is_rejected_without_creating_markers()
    {
        Assert.Null(ResolveTestRepositoryRoot(Path.Combine(Path.GetTempPath(),
            "fgo-root-resolution-unrelated", "output"), null));
    }
}
