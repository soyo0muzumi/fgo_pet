using System.Net;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Plugin.SocialSources.Platforms;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Optional authenticated session for one platform, modelled on the browser plugin's connection: the
/// plugin owns a single-flight connection object that holds the session in memory only, resolves the
/// secret lazily, and exposes a real connection test. Losing the grant or secret resets the connection
/// and invalidates in-flight results.
/// </summary>
public interface ISocialSessionConnection : IAsyncDisposable
{
    string Platform { get; }

    /// <summary>True once a live call has succeeded with the current grant generation.</summary>
    bool Connected { get; }

    /// <summary>Runs one authenticated search. Resolves the secret on first use.</summary>
    Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token);

    /// <summary>Explicit connection test. Sends one fixed public query and returns no source text.</summary>
    Task<ToolAvailability> CheckAsync(CancellationToken token);

    /// <summary>Set by the owner for the duration of one call so a revocation discards the result.</summary>
    Func<bool>? StillAuthorized { get; set; }

    /// <summary>Drops the cached secret and connection state without touching storage.</summary>
    ValueTask ResetAsync();
}

internal sealed class SocialSessionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Shared session plumbing: lazy secret resolution, generation fencing and result discarding.</summary>
internal abstract class SocialSessionConnectionBase : ISocialSessionConnection
{
    internal const int MaxSecretLength = 8192;

    private readonly string _platform;
    private readonly ICredentialReader _credentials;
    private string? _secret;
    private long _generation;
    private bool _connected;

    protected SocialSessionConnectionBase(string platform, ICredentialReader credentials,
        SocialHttpGateway gateway)
    {
        _platform = platform;
        _credentials = credentials;
        Gateway = gateway;
    }

    /// <summary>Shared HTTP boundary for derived platform protocols.</summary>
    protected SocialHttpGateway Gateway { get; }

    public string Platform => _platform;

    public bool Connected => Volatile.Read(ref _connected);

    /// <summary>Owner-supplied fence: returning false discards an in-flight result.</summary>
    public Func<bool>? StillAuthorized { get; set; }

    public async Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token)
    {
        var generation = Interlocked.Read(ref _generation);
        var secret = await SecretAsync(token).ConfigureAwait(false);
        try
        {
            var result = await SearchCoreAsync(secret, query, maxResults, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _generation) || StillAuthorized?.Invoke() == false)
                throw new SocialSourceException("SOCIAL_SESSION_CHANGED");
            Volatile.Write(ref _connected, true);
            return result;
        }
        catch (SocialSourceException error) when (error.Code is "SOCIAL_SESSION_INVALID" or "SOCIAL_SESSION_EXPIRED")
        {
            // A rejected session is unusable until it is reset; never keep retrying with it.
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ToolAvailability> CheckAsync(CancellationToken token)
    {
        var generation = Interlocked.Read(ref _generation);
        try
        {
            var secret = await SecretAsync(token).ConfigureAwait(false);
            await ProbeAsync(secret, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _generation) || StillAuthorized?.Invoke() == false)
                return new(false, "SOCIAL_SESSION_CHANGED");
            Volatile.Write(ref _connected, true);
            return new(true);
        }
        catch (SocialSourceException error)
        {
            Volatile.Write(ref _connected, false);
            return new(false, error.Code);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Volatile.Write(ref _connected, false); return new(false, "SOCIAL_SESSION_FAILED"); }
    }

    public ValueTask ResetAsync()
    {
        _secret = null;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _connected, false);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ResetAsync();

    /// <summary>Resolves the secret once per generation. Never logs, echoes or returns it.</summary>
    private async Task<string> SecretAsync(CancellationToken token)
    {
        if (_secret is { Length: > 0 } cached) return cached;
        string? stored;
        try { stored = await _credentials.ReadAsync(SocialSessionSettings.CredentialTarget(_platform), token)
            .ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new SocialSourceException("SOCIAL_SESSION_UNAVAILABLE"); }

        if (string.IsNullOrWhiteSpace(stored)) throw new SocialSourceException("SOCIAL_SESSION_MISSING");
        if (stored.Length > MaxSecretLength || stored.Any(char.IsControl))
            throw new SocialSourceException("SOCIAL_SESSION_INVALID");
        _secret = stored;
        return stored;
    }

    /// <summary>One authenticated search. Platform-specific wire format lives in the derived type.</summary>
    protected abstract Task<SocialSourceResult> SearchCoreAsync(string secret, string query, int maxResults,
        CancellationToken token);

    /// <summary>One cheap authenticated probe used by the explicit connection test.</summary>
    protected abstract Task ProbeAsync(string secret, CancellationToken token);

    protected async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request,
        CancellationToken token)
    {
        var (status, body) = await Gateway.SendAsync(request, token).ConfigureAwait(false);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SocialSourceException("SOCIAL_SESSION_EXPIRED");
        if ((int)status == 412 || status is HttpStatusCode.TooManyRequests)
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status != HttpStatusCode.OK) throw new SocialSourceException("SOCIAL_NETWORK_FAILED");
        return (status, body);
    }

    protected static SocialSourceResult Materialize(List<SocialSource> rows, string coverage, string body) =>
        new(rows, DateTimeOffset.UtcNow.ToString("O"), SocialSourceText.Sha256Hex(body),
            "response_snapshot_sha256", coverage, Exhaustive: false, Truncated: false);

    protected static SocialSource? MapRow(string platform, JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object) return null;
        // 小红书 note cards use snake_case (display_title); 小黑盒 rows use title.
        var rawTitle = XiaoheiheReader.Text(card, "display_title")
            ?? XiaoheiheReader.Text(card, "title") ?? XiaoheiheReader.Text(card, "displayTitle");
        var title = rawTitle is null ? string.Empty : SocialSourceText.StripTags(rawTitle);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;
        var rawText = XiaoheiheReader.Text(card, "desc") ?? XiaoheiheReader.Text(card, "description") ?? string.Empty;
        var text = SocialSourceText.StripTags(rawText);
        if (!SocialSourceText.SafeText(text, allowLineBreaks: true)) text = string.Empty;
        var clippedTitle = SocialSourceText.Clip(title, SocialSourceText.MaxTitleRunes, out var titleTruncated);
        var clippedText = SocialSourceText.Clip(text, SocialSourceText.MaxSummaryRunes, out var textTruncated);

        string? author = null;
        if (card.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            author = XiaoheiheReader.Text(user, "nickname") ?? XiaoheiheReader.Text(user, "username");

        string? popularity = null;
        if (card.TryGetProperty("interact_info", out var interact) && interact.ValueKind == JsonValueKind.Object)
            popularity = XiaoheiheReader.NumberOrString(interact, "liked_count");
        else if (card.TryGetProperty("interactInfo", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            popularity = XiaoheiheReader.NumberOrString(legacy, "likedCount");

        return new SocialSource(platform, clippedTitle, string.Empty, clippedText, author, null,
            popularity is null ? null : popularity + " likes", titleTruncated, textTruncated);
    }
}
