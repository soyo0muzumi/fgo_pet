using FgoPet.CodexAdapter.AppServer;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexTargetCatalogTests
{
    [Fact]
    public void Explicit_target_registration_preserves_readonly_and_stable_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new CodexTargetCatalog(Path.Combine(root, "state"));
            Assert.Throws<UnauthorizedAccessException>(() => catalog.Resolve("unknown"));
            Assert.Throws<ArgumentException>(() => catalog.Add("relative-project"));
            var target = catalog.Add(root, "Acceptance", readOnly: true);
            Assert.Equal(root, catalog.Resolve(target.TargetId));
            Assert.True(catalog.IsReadOnly(target.TargetId));
            var reloaded = new CodexTargetCatalog(Path.Combine(root, "state"));
            Assert.Equal(target.TargetId, reloaded.Add(root, "Renamed", readOnly: true).TargetId);
            Assert.Single(reloaded.List());
        }
        finally { Directory.Delete(root, true); }
    }
}
