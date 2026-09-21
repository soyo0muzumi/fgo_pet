using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

public sealed record ContentBinding(
    ContentContextKey Context,
    PersonaBundle? Persona,
    IReadOnlyList<KnowledgeEntry> Knowledge,
    IReadOnlyList<string> AppliedLayers,
    string PersonaHash,
    string KnowledgeHash);

/// <summary>Resolves the exact content version used by a dialogue turn.</summary>
public static class ContentBindingResolver
{
    public static ContentBinding Resolve(string packageRoot, string servantId, string appearanceId)
    {
        var persona = PersonaManifestReader.ReadOptional(packageRoot, servantId);
        var knowledge = KnowledgeManifestReader.ReadOptional(packageRoot, servantId, appearanceId) ?? [];
        var package = TryReadPackageManifest(packageRoot);

        var packageId = package?.PackageId ?? persona?.PackageId ?? "unknown-package";
        var packageVersion = package?.PackageVersion ?? persona?.PackageVersion ?? "0.0.0";
        var personaVersion = persona?.PersonaVersion ?? "none";
        var knowledgeVersion = KnowledgeManifestReader.ReadVersionOptional(packageRoot) ?? "none";
        var context = new ContentContextKey(
            servantId,
            packageId,
            packageVersion,
            appearanceId,
            personaVersion,
            knowledgeVersion);

        var layers = new List<string>();
        if (persona is not null)
        {
            layers.Add("servant-core");
            if (persona.FindAppearance(appearanceId) is { } overlay)
            {
                layers.Add(overlay.AppearanceId);
            }
        }

        if (knowledge.Count > 0)
        {
            layers.Add("approved-knowledge");
        }

        return new ContentBinding(
            context,
            persona,
            knowledge,
            layers,
            HashDirectory(Path.Combine(packageRoot, "persona")),
            HashDirectory(Path.Combine(packageRoot, "knowledge")));
    }

    private static PackManifestV1? TryReadPackageManifest(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return PackJson.DeserializeStrict<PackManifestV1>(File.ReadAllText(path), path);
        }
        catch (Exception error) when (error is PackFailureException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string HashDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "none";
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
