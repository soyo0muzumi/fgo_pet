using System.IO;

namespace FgoPet.App.Bootstrap;

public sealed record AppPaths(string StorageRoot, string PackagesRoot)
{
    public string RuntimeDatabasePath => Path.Combine(StorageRoot, "runtime.db");

    public static AppPaths ForCurrentUser()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoPet");
        return new AppPaths(root, Path.Combine(root, "Packages"));
    }
}
