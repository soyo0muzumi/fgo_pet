using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Browser;

/// <summary>Changes only the native visual grant in an existing, valid browser setup.</summary>
public sealed class ChromeBrowserVisualAccess(string path) : IBrowserVisualAccess
{
    public bool IsAvailable => ChromeBrowserSettings.Load(path) is not null;
    public bool Enabled => ChromeBrowserSettings.Load(path)?.VisualEnabled == true;
    public bool TrySetEnabled(bool enabled)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!IsAvailable) return false;
            JsonObject document;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 8192) return false;
                document = JsonNode.Parse(input) as JsonObject ?? throw new JsonException();
            }
            document["VisualEnabled"] = enabled;
            // A revoke/regrant must never revive an in-flight capture's authority.
            document["VisualGrantId"] = Guid.NewGuid().ToString("N");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > 8192) return false;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
