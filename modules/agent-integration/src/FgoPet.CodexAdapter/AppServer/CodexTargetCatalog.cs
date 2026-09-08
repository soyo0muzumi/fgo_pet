using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FgoPet.CodexAdapter.AppServer;

public sealed record CodexTarget(string TargetId, string DisplayName, string Directory, bool ReadOnly);

/// <summary>Explicit local target registration; filesystem paths never travel through Relay.</summary>
public sealed class CodexTargetCatalog(string stateRoot) : ICodexTargetResolver
{
    private readonly string _path = Path.Combine(Path.GetFullPath(stateRoot), "CodexAdapter", "targets.v1.json");

    public IReadOnlyList<CodexTarget> List()
    {
        if (!File.Exists(_path)) return [];
        using var stream = File.OpenRead(_path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("target_catalog_too_large");
        var targets = JsonSerializer.Deserialize<CodexTarget[]>(stream) ?? throw new InvalidDataException("target_catalog_invalid");
        if (targets.Length > 256 || targets.Any(t => t is null || t.TargetId != IdFor(t.Directory))
            || targets.Select(t => t.TargetId).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw new InvalidDataException("target_catalog_invalid");
        return targets;
    }

    public CodexTarget Add(string directory, string? displayName = null, bool readOnly = false)
    {
        directory = ValidateDirectory(directory);
        var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(directory) : displayName.Trim();
        if (name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("target_name_invalid");
        var target = new CodexTarget(IdFor(directory), name, directory, readOnly);
        // Synchronous file lock also serializes separate CLI invocations.
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var gate = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var targets = List().Where(item => item.TargetId != target.TargetId).Append(target).ToArray();
        if (targets.Length > 256) throw new InvalidDataException("target_catalog_full");
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, targets);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }

    public string Resolve(string targetId) => ValidateDirectory(Find(targetId).Directory);
    public bool IsReadOnly(string targetId) => Find(targetId).ReadOnly;
    private CodexTarget Find(string targetId) => List().SingleOrDefault(item => item.TargetId == targetId)
        ?? throw new UnauthorizedAccessException("target_not_registered");

    private static string IdFor(string directory) => "project-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24].ToLowerInvariant();

    private static string ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("target_requires_absolute_directory");
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!System.IO.Directory.Exists(full) || full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar)
            || string.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("target_requires_project_directory");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("target_reparse_point_not_supported");
        return full;
    }
}
