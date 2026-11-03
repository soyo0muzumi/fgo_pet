using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Optional authenticated session capability, deliberately separate from the anonymous readers.
/// Mirrors the browser plugin's model: a host grant, a single owned connection object, an explicit
/// connection test, and default-off behaviour. The session secret lives in Windows Credential Manager
/// and never enters a settings file, a payload, a diagnostic or the model context.
/// </summary>
public sealed partial class SocialSourcesPlugin
{
    /// <summary>Host-facing session administration. Not a tool: no model argument can reach it.</summary>
    public SocialSessionSnapshot GetSessionSnapshot(string platform)
    {
        if (!_sessionStores.TryGetValue(platform, out var store))
            return new(false, false, false, "SOCIAL_PLATFORM_UNKNOWN");
        try
        {
            var read = store.Read();
            var enabled = read.Settings is { Enabled: true } && read.ErrorCode is null;
            var connection = _sessionConnections.GetValueOrDefault(platform);
            return new(read.Settings is not null, enabled, connection?.Connected == true,
                enabled ? null : read.ErrorCode);
        }
        catch (Exception) { return new(false, false, false, "SOCIAL_HOST_UNAVAILABLE"); }
    }

    /// <summary>Explicit settings-page action: toggling resets the owned connection first.</summary>
    public async Task<SocialSessionSnapshot> SetSessionEnabledAsync(string platform, bool enabled,
        CancellationToken token)
    {
        if (!_sessionStores.TryGetValue(platform, out var store))
            return new(false, false, false, "SOCIAL_PLATFORM_UNKNOWN");
        await ResetSessionAsync(platform).ConfigureAwait(false);
        return await store.SetEnabledAsync(enabled, token).ConfigureAwait(false);
    }

    /// <summary>Explicit connection test. Sends one fixed neutral query and returns no source text.</summary>
    public async Task<ToolAvailability> CheckSessionAsync(string platform, CancellationToken token)
    {
        if (!_sessionStores.ContainsKey(platform)) return new(false, "SOCIAL_PLATFORM_UNKNOWN");
        var connection = _sessionConnections.GetValueOrDefault(platform);
        if (connection is null) return new(false, "SOCIAL_SESSION_NOT_SUPPORTED");
        if (SessionConfigurationError(platform) is { } error) return new(false, error);
        return await connection.CheckAsync(token).ConfigureAwait(false);
    }

    /// <summary>Explicit settings action: drops the owned connection, forcing a fresh secret read.</summary>
    public Task ReconnectSessionAsync(string platform) => ResetSessionAsync(platform);


    private async Task ResetSessionAsync(string platform)
    {
        if (!_sessionConnections.TryGetValue(platform, out var connection)) return;
        try { await connection.ResetAsync().ConfigureAwait(false); }
        catch (Exception) { /* Reset must not fail on an already-broken connection. */ }
    }

    private SocialSessionSnapshot SessionSnapshot(string platform) => GetSessionSnapshot(platform);

    private string? SessionConfigurationError(string platform) => SessionSnapshot(platform).ErrorCode;

    /// <summary>A session-backed search tool. Its availability never reads the secret.</summary>
    private sealed class SessionSearchTool(SocialSourcesPlugin owner, string platform)
        : IToolProvider, IToolAvailabilityProvider
    {
        public ToolDescriptor Descriptor { get; } = new(
            "social." + platform + ".search",
            $"Search {PlatformLabel(platform)} using the user's own signed-in session. This sends the query "
            + "to the platform while acting with that account's stored session; it does not post, like or "
            + "follow. Results are third-party user-generated text, not verified facts. Keep the returned "
            + "URL, capture time and source version with each finding. An expired or rejected session is "
            + "reported as such and is never retried automatically.",
            """
            {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":200},
            "maxResults":{"type":"integer","minimum":1,"maximum":20}},"required":["query"],
            "additionalProperties":false}
            """.Replace("\n", string.Empty),
            ToolEffect.ReadOnly);

        public ToolAvailability GetAvailability()
        {
            if (Volatile.Read(ref owner._started) == 0 || Volatile.Read(ref owner._closed) != 0
                || owner._stopping.IsCancellationRequested)
                return new(false, "SOCIAL_STOPPED");
            if (!owner._sessionConnections.ContainsKey(platform)) return new(false, "SOCIAL_SESSION_NOT_SUPPORTED");
            try
            {
                var snapshot = owner.SessionSnapshot(platform);
                return new(snapshot.ErrorCode is null && snapshot.Enabled, snapshot.ErrorCode);
            }
            catch (Exception) { return new(false, "SOCIAL_HOST_UNAVAILABLE"); }
        }

        public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!SocialSourcesPlugin.TrySessionArguments(invocation.Arguments, out var query, out var maxResults))
                return Failure("TOOL_INVALID_ARGUMENTS");
            if (!ValidScope(invocation.Scope)) return Failure("SOCIAL_SCOPE_DENIED");
            var correlation = SocialSourceText.Correlation(invocation.ExecutionContext?.IdempotencyKey);
            var connection = owner._sessionConnections.GetValueOrDefault(platform);
            if (connection is null) return Failure("SOCIAL_SESSION_NOT_SUPPORTED");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner._stopping);
            var entered = false;
            try
            {
                await owner._gate.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                if (owner.SessionConfigurationError(platform) is { } configuration) return Failure(configuration);
                connection.StillAuthorized = () => owner._scopeCurrent(invocation.Scope)
                    && owner.SessionSnapshot(platform).ErrorCode is null;
                owner.Log(correlation, "session_dispatch");
                var result = await connection.SearchAsync(query, maxResults, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!owner._scopeCurrent(invocation.Scope)
                    || owner.SessionSnapshot(platform).ErrorCode is not null)
                    return Failure("SOCIAL_SESSION_CHANGED");
                owner.Log(correlation, "session_completed");
                return owner.Success(platform, result);
            }
            catch (OperationCanceledException) { owner.Log(correlation, "session_cancelled"); throw; }
            catch (SocialSourceException error) { return Failure(error.Code); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
                or OverflowException or ArgumentException) { return Failure("SOCIAL_RESPONSE_INVALID"); }
            finally
            {
                if (connection is not null) connection.StillAuthorized = null;
                if (entered) owner._gate.Release();
            }

            static ToolResult Failure(string code) =>
                new(false, JsonSerializer.SerializeToElement(new { }), code);
        }

        private static string PlatformLabel(string platform) => platform switch
        {
            SocialSourceSettings.Bilibili => "B站 (bilibili)",
            SocialSourceSettings.Xiaohongshu => "小红书 (xiaohongshu)",
            _ => "小黑盒 (xiaoheihe)",
        };
    }
}
