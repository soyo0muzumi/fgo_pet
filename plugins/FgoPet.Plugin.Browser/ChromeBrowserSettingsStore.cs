using System.Text.Json;

namespace FgoPet.Plugin.Browser;

/// <summary>Host settings commands only. Does not install dependencies, connect Chrome or grant visual access.</summary>
public sealed class ChromeBrowserSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _writes;

    public ChromeBrowserSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _path = Path.GetFullPath(settingsPath);
        _writes = ChromeBrowserSettings.GetWriteGate(_path);
    }

    public ChromeBrowserSetupState Read() => ChromeBrowserSettings.Read(_path);
    public ChromeBrowserSettings? LoadEnabled() => ChromeBrowserSettings.Load(_path);

    /// <summary>A null enabled value preserves the current grant under the writer lock.</summary>
    public async ValueTask<ChromeBrowserSetupState> SaveAsync(string nodePath, string serverPath,
        string? userDataDirectory, bool? enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nodePath = nodePath?.Trim() ?? "";
        serverPath = serverPath?.Trim() ?? "";
        userDataDirectory = string.IsNullOrWhiteSpace(userDataDirectory) ? null : userDataDirectory.Trim();
        if (!ChromeBrowserSettings.ValidPath(nodePath) || !ChromeBrowserSettings.ValidPath(serverPath)
            || userDataDirectory is not null && !ChromeBrowserSettings.ValidPath(userDataDirectory))
            return new(Read().Settings, "BROWSER_SETTINGS_INVALID");
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var previous = Read();
            var desiredEnabled = enabled ?? previous.Enabled;
            var sameSetup = previous.Settings is { Enabled: true } current && desiredEnabled
                && current.NodePath == nodePath && current.ServerPath == serverPath
                && current.UserDataDirectory == userDataDirectory;
            var next = new ChromeBrowserSettings(desiredEnabled, nodePath, serverPath, userDataDirectory,
                // Existing text-only grants are not silently upgraded by saving connection paths.
                VisualEnabled: desiredEnabled && (sameSetup ? previous.Settings!.VisualEnabled : !previous.Enabled),
                VisualGrantId: sameSetup ? previous.Settings!.VisualGrantId : Guid.NewGuid().ToString("N"),
                AuthorizationId: Guid.NewGuid().ToString("N"));
            return await WriteAsync(next, previous, cancellationToken);
        }
        finally { _writes.Release(); }
    }

    public async ValueTask<ChromeBrowserSetupState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var previous = Read();
            if (previous.Settings is not { } settings) return previous;
            var next = settings with
            {
                Enabled = enabled,
                AuthorizationId = Guid.NewGuid().ToString("N"),
                // An explicit browser enable/re-enable grants the disclosed text and image scope.
                VisualEnabled = enabled,
                VisualGrantId = enabled && settings.Enabled ? settings.VisualGrantId : Guid.NewGuid().ToString("N"),
            };
            return await WriteAsync(next, previous, cancellationToken);
        }
        finally { _writes.Release(); }
    }

    private async ValueTask<ChromeBrowserSetupState> WriteAsync(ChromeBrowserSettings settings,
        ChromeBrowserSetupState previous, CancellationToken cancellationToken)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > 8192) return new(previous.Settings, "BROWSER_SETTINGS_INVALID");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var current = Read();
            if (current.SourceVersion != previous.SourceVersion || current.Settings != previous.Settings
                || current.ErrorCode != previous.ErrorCode)
                return new(current.Settings, "BROWSER_SETTINGS_CHANGED");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            return Read();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(previous.Settings, "BROWSER_SETTINGS_WRITE_FAILED"); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
