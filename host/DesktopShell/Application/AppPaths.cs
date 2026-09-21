using System.IO;

namespace FgoPet.App.Bootstrap;

public sealed record AppPaths(string StorageRoot, string PackagesRoot)
{
    public string RuntimeDatabasePath => Path.Combine(StorageRoot, "runtime.db");

    public static AppPaths ForCurrentUser()
    {
        var root = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoPet");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("FGO_PET_STATE_ROOT must be absolute.");
        return new AppPaths(root, Path.Combine(root, "Packages"));
    }
}
