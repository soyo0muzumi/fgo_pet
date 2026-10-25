using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FgoPet.Plugin.Browser;

/// <summary>Single-flight stdio connection, owned and serialized by ChromeBrowserPlugin. No shell or auto-install.</summary>
internal sealed class ChromeMcpConnection : IChromeMcpConnection
{
    private Process? _process;
    private ChromeBrowserSettings? _settings;
    private Task? _stderr;
    private long _id;
    private string? _scratch;
    private readonly char[] _readBuffer = new char[8192];
    private int _position, _length;

    public async ValueTask<JsonElement> CallAsync(ChromeBrowserSettings settings, string name, JsonElement arguments, CancellationToken token)
    {
        if (name is not ("list_pages" or "new_page" or "take_snapshot" or "take_screenshot")) throw new BrowserException("BROWSER_TOOL_DENIED");
        if (name == "take_screenshot" && !settings.VisualEnabled) throw new BrowserException("BROWSER_VISUAL_NOT_ENABLED");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (_settings != settings || _process is null || _process.HasExited)
            {
                await ResetAsync();
                Start(settings);
                var initialized = await Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "fgo-pet-browser", version = "1.0.0" } }, deadline.Token);
                if (initialized.GetProperty("serverInfo").GetProperty("version").GetString() != ChromeBrowserSettings.ServerVersion
                    || initialized.GetProperty("protocolVersion").GetString() is not ("2025-06-18" or "2025-03-26" or "2024-11-05"))
                    throw new BrowserException("BROWSER_SERVER_VERSION_UNSUPPORTED");
                await Write(new { jsonrpc = "2.0", method = "notifications/initialized" }, deadline.Token);
                var available = await Request("tools/list", new { }, deadline.Token);
                var names = available.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
                if (!new[] { "list_pages", "new_page", "take_snapshot" }.All(names.Contains))
                    throw new BrowserException("BROWSER_SERVER_TOOLS_MISSING");
                if (settings.VisualEnabled && !names.Contains("take_screenshot")) throw new BrowserException("BROWSER_SERVER_TOOLS_MISSING");
                _settings = settings;
            }
            return await Request("tools/call", new { name, arguments }, deadline.Token,
                name == "take_screenshot" ? 2_800_000 : 2_097_152);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { await ResetAsync(); throw new BrowserException("BROWSER_CONNECTION_TIMEOUT"); }
        catch (OperationCanceledException) { await ResetAsync(); throw; }
        catch (BrowserException) { await ResetAsync(); throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException
            or KeyNotFoundException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { await ResetAsync(); throw new BrowserException("BROWSER_CONNECTION_FAILED"); }
    }

    private void Start(ChromeBrowserSettings settings)
    {
        if (!Path.IsPathFullyQualified(settings.NodePath) || !Path.IsPathFullyQualified(settings.ServerPath)
            || !File.Exists(settings.NodePath) || !File.Exists(settings.ServerPath)) throw new BrowserException("BROWSER_SETUP_REQUIRED");
        if (settings.UserDataDirectory is { } directory && (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)))
            throw new BrowserException("BROWSER_SETUP_REQUIRED");
        var info = new ProcessStartInfo(settings.NodePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(settings.ServerPath)!
        };
        foreach (var arg in new[] { settings.ServerPath, "--autoConnect", "--experimental-structured-content",
            "--no-usage-statistics", "--no-performance-crux", "--javascript-evaluation=false", "--file-navigations=false" })
            info.ArgumentList.Add(arg);
        if (settings.VisualEnabled)
        {
            foreach (var arg in new[] { "--screenshot-max-width=1024", "--screenshot-max-height=1024", "--screenshot-format=jpeg", "--screenshot-quality=60" })
                info.ArgumentList.Add(arg);
            _scratch = Path.Combine(Path.GetTempPath(), "fgopet-browser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            // Upstream may spool oversized images. Keep such files in this process-owned directory.
            info.Environment["TEMP"] = _scratch; info.Environment["TMP"] = _scratch; info.Environment["TMPDIR"] = _scratch;
        }
        if (settings.UserDataDirectory is { } profile)
        {
            info.ArgumentList.Add("--user-data-dir");
            info.ArgumentList.Add(profile);
        }
        // The server must not discover ambient config, injected Node hooks, or diagnostic output files.
        foreach (var name in new[] { "NODE_OPTIONS", "NODE_PATH", "DEBUG", "NODE_DEBUG", "PLUGIN_DATA" }) info.Environment.Remove(name);
        info.Environment["CHROME_DEVTOOLS_MCP_NO_CONFIG_DISCOVERY"] = "1";
        info.Environment["CHROME_DEVTOOLS_MCP_NO_USAGE_STATISTICS"] = "1";
        info.Environment["CHROME_DEVTOOLS_MCP_NO_UPDATE_CHECKS"] = "1";
        _process = Process.Start(info) ?? throw new BrowserException("BROWSER_START_FAILED");
        _stderr = DrainStderr(_process.StandardError);
    }

    private async Task<JsonElement> Request(string method, object parameters, CancellationToken token, int maximumChars = 2_097_152)
    {
        var id = ++_id;
        await Write(new { jsonrpc = "2.0", id, method, @params = parameters }, token);
        for (var count = 0; count < 64; count++)
        {
            using var message = JsonDocument.Parse(await ReadLine(token, maximumChars), new JsonDocumentOptions { MaxDepth = 128 });
            var root = message.RootElement;
            if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new BrowserException("BROWSER_INVALID_PROTOCOL");
            if (root.TryGetProperty("method", out _))
            {
                // No sampling, roots, elicitation or other server-initiated authority is negotiated.
                if (root.TryGetProperty("id", out var serverId))
                    await Write(new { jsonrpc = "2.0", id = serverId, error = new { code = -32601, message = "Unsupported method" } }, token);
                continue;
            }
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out var number) || number != id)
                throw new BrowserException("BROWSER_INVALID_PROTOCOL");
            if (root.TryGetProperty("error", out _)) throw new BrowserException("BROWSER_PROTOCOL_FAILED");
            return root.GetProperty("result").Clone();
        }
        throw new BrowserException("BROWSER_PROTOCOL_LIMIT");
    }

    private async Task Write(object message, CancellationToken token)
    {
        await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
    }

    private async Task<string> ReadLine(CancellationToken token, int maximumChars)
    {
        var result = new StringBuilder();
        while (true)
        {
            if (_position == _length)
            {
                _length = await _process!.StandardOutput.ReadAsync(_readBuffer.AsMemory(), token);
                _position = 0;
                if (_length == 0) throw new BrowserException("BROWSER_CONNECTION_CLOSED");
            }
            var newline = Array.IndexOf(_readBuffer, '\n', _position, _length - _position);
            var end = newline >= 0 ? newline : _length;
            if (result.Length + end - _position > maximumChars) throw new BrowserException("BROWSER_PROTOCOL_LIMIT");
            result.Append(_readBuffer, _position, end - _position);
            _position = newline >= 0 ? newline + 1 : end;
            if (newline >= 0) return result.ToString().TrimEnd('\r');
        }
    }

    private static async Task DrainStderr(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public async ValueTask ResetAsync()
    {
        var process = _process; _process = null; _settings = null; _position = _length = 0;
        if (process is null) { CleanScratch(); return; }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_stderr is not null) await _stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException) { }
        finally { process.Dispose(); _stderr = null; CleanScratch(); }
    }
    private void CleanScratch()
    {
        var directory = _scratch; _scratch = null;
        if (directory is null) return;
        try
        {
            var root = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(root).StartsWith("fgopet-browser-", StringComparison.Ordinal)
                || !Directory.Exists(root)) return;
            // Never recurse through a link, even inside an owned scratch directory.
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var next))
            {
                if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var item in Directory.EnumerateFileSystemEntries(next))
                {
                    if (!Path.GetFullPath(item).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return;
                    if (Directory.Exists(item)) pending.Push(item);
                }
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Leave unknown contents untouched. */ }
    }
    public ValueTask DisposeAsync() => ResetAsync();
}
