using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Browser;

public sealed partial class ChromeBrowserPlugin
{
    private async ValueTask<ToolResult> InspectImage(ToolInvocation invocation, ChromeBrowserSettings settings,
        Func<string, object, ValueTask<JsonElement>> call, CancellationToken token)
    {
        if (!settings.VisualEnabled || _imageSink is null) throw new BrowserImageException("BROWSER_VISUAL_NOT_ENABLED");
        var runId = invocation.ExecutionContext?.RunId;
        if (string.IsNullOrWhiteSpace(runId)) throw new BrowserImageException("IMAGE_RUN_DENIED");
        if (_imageSink.Check(invocation.Scope, runId) is { } denied) throw new BrowserImageException(denied);
        var args = invocation.Arguments;
        var page = args.GetProperty("pageId").GetInt32();
        var id = args.GetProperty("snapshotId").GetString();
        if (id is null || !_snapshots.TryGetValue(id, out var snapshot) || snapshot.PageId != page
            || snapshot.Scope != invocation.Scope || snapshot.Grant != settings
            || snapshot.CapturedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-5))
            throw new BrowserImageException("BROWSER_CURSOR_EXPIRED");
        var uid = args.TryGetProperty("uid", out var node) ? node.GetString() : null;
        if (uid is not null && (uid.Length is < 1 or > 64 || !uid.All(c => char.IsAsciiDigit(c) || c == '_')
            || !Regex.IsMatch(snapshot.Text, @"\buid=" + Regex.Escape(uid) + @"(?=\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
            throw new BrowserImageException("BROWSER_ELEMENT_UNAVAILABLE");
        var before = ParsePages(await call("list_pages", new { }));
        if (!before.Any(p => p.id == page && p.url == snapshot.Url)) throw new BrowserException("BROWSER_SOURCE_CHANGED");
        var arguments = new Dictionary<string, object> { ["pageId"] = page, ["format"] = "jpeg", ["quality"] = 60, ["fullPage"] = false };
        if (uid is not null) arguments["uid"] = uid;
        var captured = await call("take_screenshot", arguments);
        var after = ParsePages(await call("list_pages", new { }));
        if (!after.Any(p => p.id == page && p.url == snapshot.Url)) throw new BrowserException("BROWSER_SOURCE_CHANGED");
        // Never follow a server-returned file path. Oversized captures are not silently read from disk.
        var blocks = captured.GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "image").ToArray();
        if (blocks.Length != 1) throw new BrowserImageException("BROWSER_IMAGE_UNAVAILABLE");
        var mime = blocks[0].GetProperty("mimeType").GetString();
        var data = blocks[0].GetProperty("data").GetString();
        if (mime is not ("image/png" or "image/jpeg") || data is null || data.Length > 2_666_668)
            throw new BrowserImageException("BROWSER_IMAGE_TOO_LARGE");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw new BrowserImageException("BROWSER_IMAGE_INVALID"); }
        if (bytes.Length is < 1 or >= 2_000_000) throw new BrowserImageException("BROWSER_IMAGE_TOO_LARGE");
        var generation = _connectionGeneration;
        bool Authorized() => generation == _connectionGeneration && settings == _settings() && _scopeCurrent(invocation.Scope)
            && _started && !_lifetime.IsCancellationRequested;
        token.ThrowIfCancellationRequested();
        var image = _imageSink.Accept(invocation.Scope, runId, bytes, mime, Authorized, token);
        return new(true, JsonSerializer.SerializeToElement(new { pageId = page, snapshotId = id, url = snapshot.Url,
            title = snapshot.Title, capturedAtUtc = DateTimeOffset.UtcNow, uid, width = image.Width, height = image.Height,
            byteLength = image.ByteLength, detail = "low", coverage = uid is null ? "viewport" : "rendered_element",
            note = "Rendered page pixels attached to the next model request. Not the original image file or an OCR guarantee." })) { Image = image.Reference };
    }
}
