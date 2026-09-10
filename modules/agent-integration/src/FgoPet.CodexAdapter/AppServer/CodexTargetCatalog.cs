using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FgoPet.CodexAdapter.AppServer;

public sealed record CodexTarget(
    string TargetId,
    string DisplayName,
    string Directory,
    bool ReadOnly,
    string? ProjectName = null,
    IReadOnlyList<string>? Branches = null,
    string? CurrentBranch = null,
    string? Revision = null,
    string? Access = null,
    string? Source = null,
    DateTimeOffset? RefreshedAtUtc = null,
    string? ContextVersion = null);

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
        return targets.Select(Describe).ToArray();
    }

    public CodexTarget Add(string directory, string? displayName = null, bool readOnly = false)
    {
        directory = ValidateDirectory(directory);
        var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(directory) : displayName.Trim();
        if (name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("target_name_invalid");
        var target = Describe(new CodexTarget(IdFor(directory), name, directory, readOnly));
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
    public string? GetContextVersion(string targetId) => Find(targetId).ContextVersion;

    private CodexTarget Find(string targetId) => List().SingleOrDefault(item => item.TargetId == targetId)
        ?? throw new UnauthorizedAccessException("target_not_registered");

    private static CodexTarget Describe(CodexTarget target)
    {
        var projectName = new DirectoryInfo(target.Directory).Name;
        var branches = Array.Empty<string>();
        string? currentBranch = null;
        string? revision = null;
        if (Directory.Exists(Path.Combine(target.Directory, ".git")) || File.Exists(Path.Combine(target.Directory, ".git")))
        {
            branches = GitLines(target.Directory, "for-each-ref", "--format=%(refname:short)", "refs/heads/");
            currentBranch = GitValue(target.Directory, "symbolic-ref", "--short", "HEAD");
            revision = GitValue(target.Directory, "rev-parse", "HEAD");
        }

        var contextVersion = ContextVersionFor(target.TargetId, target.ReadOnly, projectName, branches, currentBranch, revision);
        return target with
        {
            ProjectName = projectName,
            Branches = branches,
            CurrentBranch = currentBranch,
            Revision = revision,
            Access = target.ReadOnly ? "read-only" : "workspace-write",
            Source = "local-adapter",
            RefreshedAtUtc = DateTimeOffset.UtcNow,
            ContextVersion = contextVersion,
        };
    }

    private static string[] GitLines(string directory, params string[] arguments)
    {
        var output = GitValue(directory, arguments);
        return output is null
            ? Array.Empty<string>()
            : output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value.Length <= 256 && !value.Any(char.IsControl))
                .Take(128)
                .ToArray();
    }

    private static string? GitValue(string directory, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null || !process.WaitForExit(1500))
            {
                try { process?.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            _ = process.StandardError.ReadToEnd();
            return process.ExitCode == 0 && output.Length <= 4096 && !output.Any(character => char.IsControl(character) && character != '\r' && character != '\n')
                ? output
                : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string ContextVersionFor(
        string targetId,
        bool readOnly,
        string projectName,
        IReadOnlyList<string> branches,
        string? currentBranch,
        string? revision)
    {
        var material = string.Join('\n',
            targetId,
            readOnly ? "read-only" : "workspace-write",
            projectName,
            currentBranch ?? string.Empty,
            revision ?? string.Empty,
            string.Join('\n', branches.Order(StringComparer.Ordinal)));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return "context-" + digest[..24].ToLowerInvariant();
    }

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