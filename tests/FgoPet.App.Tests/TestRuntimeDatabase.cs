using FgoPet.Infrastructure.Persistence;

namespace FgoPet.App.Tests;

internal static class TestRuntimeDatabase
{
    // A temporary fixture owns its handles until each using scope ends. It must
    // not depend on process-wide ClearAllPools racing other parallel fixtures.
    internal static RuntimeDatabase Create(string path) => new(path, pooling: false);
}
