namespace FgoPet.CodexAdapter.AppServer;

internal static class CodexExecutableLocator
{
    public static string? FindInCodexBin(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) return null;

            var direct = Path.Combine(root, "codex.exe");
            if (File.Exists(direct)) return direct;

            return Directory.EnumerateDirectories(root)
                .Select(directory => new { Directory = directory, Candidate = Path.Combine(directory, "codex.exe") })
                .Where(item => File.Exists(item.Candidate))
                .OrderByDescending(item => LastWriteTimeUtc(item.Directory))
                .ThenByDescending(item => item.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Candidate)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static DateTime LastWriteTimeUtc(string directory)
    {
        try { return Directory.GetLastWriteTimeUtc(directory); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
