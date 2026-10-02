using System.IO;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.App.Portraits.Live2D;

/// <summary>Private, disposable copy of trusted SDK code and one validated data-only model.</summary>
internal sealed class Live2DSession(string root) : IDisposable
{
    public string Root { get; } = root;

    public static Live2DSession? TryCreate(
        string appearanceRoot,
        AppearanceManifestV3 appearance,
        string runtimeRoot,
        string sessionsRoot,
        CancellationToken cancellationToken)
    {
        if (appearance.Live2D is not { ModelPath: "live2d/mash_casual.model3.json" } live2d
            || !File.Exists(Path.Combine(runtimeRoot, "index.html"))
            || !File.Exists(Path.Combine(runtimeRoot, "app.js"))
            || !File.Exists(Path.Combine(runtimeRoot, "live2dcubismcore.js")))
        {
            return null;
        }
        var validation = AppearanceValidator.Validate(appearance, appearanceRoot);
        if (!validation.IsValid)
        {
            return null;
        }

        Directory.CreateDirectory(sessionsRoot);
        var destination = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            CopyTree(runtimeRoot, destination, cancellationToken);
            var modelRoot = Path.Combine(destination, "Resources", "mash_casual");
            foreach (var resource in live2d.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = resource.RelativePath["live2d/".Length..];
                var source = Path.Combine(appearanceRoot, resource.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(modelRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            return new Live2DSession(destination);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public void Dispose() => TryDelete(Root);

    private static void CopyTree(string sourceRoot, string targetRoot, CancellationToken token)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Live2D runtime contains a linked file.");
            }
            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
