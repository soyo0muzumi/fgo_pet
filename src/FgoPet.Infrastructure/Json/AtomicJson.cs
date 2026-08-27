using System.Text;
using System.Text.Json;

namespace FgoPet.Infrastructure.Json;

/// <summary>Reads/writes small JSON state files atomically, quarantining corrupt files.</summary>
internal static class AtomicJson
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string? ReadOrNull(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path, Utf8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Quarantine(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(path, $"{path}.corrupt.{stamp}");
        }
        catch (IOException)
        {
            // best effort
        }
    }

    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Utf8.GetBytes(json));
        File.Move(temp, path, overwrite: true);
    }
}