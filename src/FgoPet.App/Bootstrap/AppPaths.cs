using System.IO;

namespace FgoPet.App.Bootstrap;

public sealed record AppPaths(string StorageRoot, string PackagesRoot)
{
    public static AppPaths ForCurrentUser()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoPet");
        return new AppPaths(root, Path.Combine(root, "Packages"));
    }
}
