using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Browser;

public sealed partial class ChromeBrowserPlugin
{
    /// <summary>Local readiness only. Does not connect or inspect a page, or imply Chrome consent.</summary>
    public ToolAvailability GetAvailability() => GetAvailability(visual: false);

    private ToolAvailability GetAvailability(bool visual)
    {
        try
        {
            if (!_started || _lifetime.IsCancellationRequested) return new(false, "BROWSER_STOPPED");
            if (ConfigurationError() is { } error) return new(false, error);
            var settings = _settings();
            if (settings is not { Enabled: true }) return new(false, "BROWSER_NOT_ENABLED");
            if (!ChromeBrowserSettings.ValidPath(settings.NodePath) || !ChromeBrowserSettings.ValidPath(settings.ServerPath)
                || settings.UserDataDirectory is not null && !ChromeBrowserSettings.ValidPath(settings.UserDataDirectory))
                return new(false, "BROWSER_SETTINGS_INVALID");
            if (ChromeBrowserSettings.DependencyError(settings) is { } missing) return new(false, missing);
            if (visual && (!settings.VisualEnabled || _imageSink is null)) return new(false, "BROWSER_VISUAL_NOT_ENABLED");
            return new(true);
        }
        catch (Exception) { return new(false, "BROWSER_HOST_UNAVAILABLE"); }
    }

    /// <summary>Explicit host settings action. Tests the configured connection; returns no tab data.</summary>
    public ValueTask<ToolAvailability> CheckConnectionAsync(CancellationToken cancellationToken = default)
        => CheckConnectionAsync(reconnect: false, cancellationToken);

    /// <summary>Explicit host settings action. Resets only the owned connection before checking it.</summary>
    public ValueTask<ToolAvailability> ReconnectAsync(CancellationToken cancellationToken = default)
        => CheckConnectionAsync(reconnect: true, cancellationToken);

    private async ValueTask<ToolAvailability> CheckConnectionAsync(bool reconnect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlation = Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var entered = false;
        RecordDiagnostic(correlation, "check_received");
        try
        {
            await _gate.WaitAsync(deadline.Token);
            entered = true;
            var readiness = GetAvailability();
            if (!readiness.IsAvailable)
            {
                await Reset();
                return Failure(readiness.ErrorCode ?? "BROWSER_HOST_UNAVAILABLE");
            }
            var settings = _settings();
            if (settings is not { Enabled: true })
            {
                await Reset();
                return Failure("BROWSER_AUTHORIZATION_CHANGED");
            }
            if (reconnect) await Reset();
            deadline.Token.ThrowIfCancellationRequested();
            if (settings != _settings() || ConfigurationError() is not null)
                return Failure("BROWSER_AUTHORIZATION_CHANGED");
            RecordDiagnostic(correlation, "check_protocol_call");
            var response = await _connection.CallAsync(settings, "list_pages", JsonSerializer.SerializeToElement(new { }), deadline.Token);
            if (settings != _settings() || ConfigurationError() is not null)
            {
                await Reset();
                return Failure("BROWSER_AUTHORIZATION_CHANGED");
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (response.TryGetProperty("isError", out var failed) && failed.GetBoolean())
                throw new BrowserException("BROWSER_OPERATION_FAILED");
            if (!response.TryGetProperty("structuredContent", out var structured)
                || !structured.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
                throw new BrowserException("BROWSER_INVALID_PROTOCOL");
            RecordDiagnostic(correlation, "check_completed");
            return new(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            if (entered) await Reset();
            return Failure("BROWSER_CONNECTION_TIMEOUT");
        }
        catch (OperationCanceledException)
        {
            if (entered) await Reset();
            RecordDiagnostic(correlation, "check_cancelled");
            throw;
        }
        catch (BrowserException error)
        {
            if (entered) await Reset();
            return Failure(error.Code);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            if (entered) await Reset();
            return Failure("BROWSER_CONNECTION_FAILED");
        }
        finally { if (entered) _gate.Release(); }

        ToolAvailability Failure(string code)
        {
            RecordDiagnostic(correlation, "check_failed", code);
            return new(false, code);
        }
    }

    private string? ConfigurationError()
    {
        try
        {
            return _configurationError?.Invoke() switch
            {
                null => null,
                "BROWSER_NOT_CONFIGURED" => "BROWSER_NOT_CONFIGURED",
                "BROWSER_NOT_ENABLED" => "BROWSER_NOT_ENABLED",
                "BROWSER_SETTINGS_INVALID" => "BROWSER_SETTINGS_INVALID",
                "BROWSER_SETTINGS_UNREADABLE" => "BROWSER_SETTINGS_UNREADABLE",
                "BROWSER_DEPENDENCY_MISSING" => "BROWSER_DEPENDENCY_MISSING",
                "BROWSER_PROFILE_MISSING" => "BROWSER_PROFILE_MISSING",
                _ => "BROWSER_HOST_UNAVAILABLE",
            };
        }
        catch (Exception) { return "BROWSER_HOST_UNAVAILABLE"; }
    }

    private void RecordDiagnostic(string correlation, string stage, string? code = null)
    {
        try { _diagnostics?.Invoke(new(DateTimeOffset.UtcNow, correlation, stage, code)); }
        catch (Exception) { }
    }
}
