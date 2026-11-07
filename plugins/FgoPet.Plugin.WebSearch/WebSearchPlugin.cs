using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;

namespace FgoPet.Plugin.WebSearch;

/// <summary>Bounded facade over the shipped, keyless Python search helper. No shell or automatic installation.</summary>
public sealed class WebSearchPlugin : IFgoPetPlugin, IDisposable
{
    public const string PluginId = "firstparty.web-search";
    private const int MaxResponseBytes = 131072;
    private const int MaxPayloadBytes = 49152;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Func<WebSearchSettings?> _settings;
    private readonly Func<ToolScope, bool> _scopeCurrent;
    private readonly IBoundedProcessRunner _runner;
    private readonly string _workerPath;
    private readonly Func<string?>? _configurationError;
    private readonly Action<WebSearchDiagnostic>? _diagnostics;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopping;
    private CancellationTokenRegistration _shutdown;
    private int _started;
    private int _closed;

    public WebSearchPlugin(Func<WebSearchSettings?> settings, Func<ToolScope, bool> scopeCurrent,
        IBoundedProcessRunner runner, string workerPath, Action<WebSearchDiagnostic>? diagnostics = null,
        Func<string?>? configurationError = null)
        : this(settings, scopeCurrent, runner, workerPath, TimeSpan.FromSeconds(45), diagnostics, configurationError) { }

    internal WebSearchPlugin(Func<WebSearchSettings?> settings, Func<ToolScope, bool> scopeCurrent,
        IBoundedProcessRunner runner, string workerPath, TimeSpan timeout,
        Action<WebSearchDiagnostic>? diagnostics = null, Func<string?>? configurationError = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scopeCurrent);
        ArgumentNullException.ThrowIfNull(runner);
        if (!Path.IsPathFullyQualified(workerPath)) throw new ArgumentException("Search helper path must be absolute.", nameof(workerPath));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(45)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _settings = settings;
        _scopeCurrent = scopeCurrent;
        _runner = runner;
        _workerPath = workerPath;
        _configurationError = configurationError;
        _diagnostics = diagnostics;
        _timeout = timeout;
        _stopping = _lifetime.Token;
        Contributions = PluginContributions.Empty with { Tools = [new SearchTool(this)] };
    }

    public PluginManifest Manifest { get; } = new(PluginId, "1.1.0", 1, []);
    public PluginContributions Contributions { get; }

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _stopping.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _shutdown = stoppingToken.Register(Close);
            if (Volatile.Read(ref _closed) != 0) _shutdown.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public ToolAvailability GetAvailability()
    {
        try
        {
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested)
                return new(false, "WEB_SEARCH_STOPPED");
            var code = ConfigurationError(HostGrant());
            return code is null ? new(true) : new(false, code);
        }
        catch (SearchException error) { return new(false, error.Code); }
    }

    /// <summary>A bounded consumer may reuse this exact public-Web grant; revocation and scope changes invalidate it.</summary>
    public Func<bool>? CaptureAuthorization(ToolScope scope)
    {
        try
        {
            var grant = HostGrant();
            if (!GetAvailability().IsAvailable || !ValidScope(scope) || !HostScopeCurrent(scope)) return null;
            return () =>
            {
                try { return GetAvailability().IsAvailable && grant == HostGrant() && HostScopeCurrent(scope); }
                catch (SearchException) { return false; }
            };
        }
        catch (SearchException) { return null; }
    }

    /// <summary>Explicit settings-page test. Sends only a fixed public query, never conversation data.</summary>
    public async ValueTask<ToolAvailability> CheckConnectionAsync(CancellationToken token)
    {
        var result = await InvokeBoundedAsync("DuckDuckGo", 1, null, CreateCorrelation(Guid.NewGuid().ToString("N")), token);
        return new(result.Success, result.ErrorCode);
    }

    private ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryArguments(invocation.Arguments, out var query, out var maxResults))
            return ValueTask.FromResult(FailureResult("TOOL_INVALID_ARGUMENTS"));
        if (!ValidScope(invocation.Scope)) return ValueTask.FromResult(FailureResult("WEB_SEARCH_SCOPE_DENIED"));
        var correlation = CreateCorrelation(invocation.ExecutionContext?.IdempotencyKey ?? Guid.NewGuid().ToString("N"));
        return InvokeBoundedAsync(query, maxResults, invocation.Scope, correlation, token);
    }

    // Match the bounded process runner's 24-character uppercase hexadecimal correlation contract.
    private static string CreateCorrelation(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private async ValueTask<ToolResult> InvokeBoundedAsync(string query, int maxResults, ToolScope? scope,
        string correlation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Log("received");
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested)
            return Failure("WEB_SEARCH_STOPPED");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping);
        deadline.CancelAfter(_timeout);
        var entered = false;
        try
        {
            Log("queued");
            await _gate.WaitAsync(deadline.Token);
            entered = true;
            var grant = HostGrant();
            var configurationCode = ConfigurationError(grant);
            if (configurationCode is not null) return Failure(configurationCode);
            if (scope is not null && !HostScopeCurrent(scope)) return Failure("WEB_SEARCH_SCOPE_DENIED");
            if (!Current()) return Failure("WEB_SEARCH_AUTHORIZATION_CHANGED");
            Log("dispatch");
            Log("process_start");
            var request = new BoundedProcessRequest(_workerPath, [], Path.GetDirectoryName(_workerPath)!,
                _timeout, MaxOutputBytes: MaxResponseBytes, MaxProcesses: 1)
            {
                StandardInput = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, query, maxResults }),
                Correlation = correlation,
                BeforeStart = () =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!Current()) throw new SearchException("WEB_SEARCH_AUTHORIZATION_CHANGED");
                }
            };
            var response = await _runner.RunAsync(request, deadline.Token);
            Log("process_result");
            deadline.Token.ThrowIfCancellationRequested();
            if (!Current()) return Failure("WEB_SEARCH_AUTHORIZATION_CHANGED");
            if (response.Exit == BoundedProcessExit.TimedOut) return Failure("WEB_SEARCH_TIMEOUT");
            if (!response.Started || response.Exit != BoundedProcessExit.Completed) return Failure("WEB_SEARCH_RUNTIME_FAILED");
            if (response.Truncated || Encoding.UTF8.GetByteCount(response.StandardOutput) > MaxResponseBytes)
                return Failure("WEB_SEARCH_RESPONSE_TOO_LARGE");
            var bytes = Encoding.UTF8.GetBytes(response.StandardOutput);
            using var envelope = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root)
                || !root.TryGetProperty("protocolVersion", out var protocol) || !protocol.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Failure("WEB_SEARCH_RESPONSE_INVALID");
            if (!success.GetBoolean())
            {
                var code = root.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                return Failure(code is "WEB_SEARCH_NETWORK_FAILED" or "WEB_SEARCH_RATE_LIMITED" or "WEB_SEARCH_TIMEOUT"
                    or "WEB_SEARCH_RESPONSE_INVALID" or "WEB_SEARCH_RESPONSE_TOO_LARGE" or "WEB_SEARCH_RUNTIME_MISSING"
                    or "WEB_SEARCH_RUNTIME_INVALID" ? code : "WEB_SEARCH_RUNTIME_FAILED");
            }
            if (response.ExitCode != 0) return Failure("WEB_SEARCH_RUNTIME_FAILED");
            var sources = ParseSources(bytes, maxResults, out var truncated);
            var capturedAtUtc = DateTimeOffset.UtcNow;
            var sourceVersion = Convert.ToHexString(SHA256.HashData(bytes));
            JsonElement payload;
            while (true)
            {
                payload = JsonSerializer.SerializeToElement(new
                {
                    results = sources,
                    returnedResults = sources.Count,
                    capturedAtUtc,
                    sourceVersion,
                    sourceVersionKind = "search_snapshot_sha256",
                    coverage = "search_snippets",
                    exhaustive = false,
                    truncated
                }, Json);
                if (Encoding.UTF8.GetByteCount(payload.GetRawText()) <= MaxPayloadBytes) break;
                if (sources.Count == 0) return Failure("WEB_SEARCH_RESULT_TOO_LARGE");
                // Preserve complete source entries and disclose lower-ranked omissions before the host's 64-KiB cap.
                sources.RemoveAt(sources.Count - 1);
                truncated = true;
            }
            if (!Current()) return Failure("WEB_SEARCH_AUTHORIZATION_CHANGED");
            deadline.Token.ThrowIfCancellationRequested();
            Log("completed");
            return new(true, payload);

            bool Current() => grant == HostGrant() && (scope is null || HostScopeCurrent(scope));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_stopping.IsCancellationRequested)
        { return Failure("WEB_SEARCH_TIMEOUT"); }
        catch (OperationCanceledException) { Log("cancelled"); throw; }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
        { Log("cancelled"); throw new OperationCanceledException(_stopping); }
        catch (SearchException error) { return Failure(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Failure("WEB_SEARCH_RUNTIME_FAILED"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Failure("WEB_SEARCH_RESPONSE_INVALID"); }
        finally { if (entered) _gate.Release(); }

        void Log(string stage, string? code = null)
        {
            // A metadata observer cannot acquire authority or alter a dispatched operation's outcome.
            try { _diagnostics?.Invoke(new(DateTimeOffset.UtcNow, correlation, stage, code)); }
            catch (Exception) { }
        }
        ToolResult Failure(string code)
        {
            Log("failed", code);
            return FailureResult(code);
        }
    }

    private static ToolResult FailureResult(string code) => new(false, JsonSerializer.SerializeToElement(new { }), code);

    private string? ConfigurationError(WebSearchSettings? grant)
    {
        try
        {
            if (!WebSearchSettings.IsAuthorized(grant))
                return _configurationError?.Invoke() ?? (grant is null ? "WEB_SEARCH_NOT_CONFIGURED"
                    : grant.Enabled ? "WEB_SEARCH_CONFIG_INVALID" : "WEB_SEARCH_NOT_ENABLED");
            return File.Exists(_workerPath) ? null : "WEB_SEARCH_RUNTIME_MISSING";
        }
        catch (Exception) { throw new SearchException("WEB_SEARCH_HOST_UNAVAILABLE"); }
    }

    private WebSearchSettings? HostGrant()
    {
        try { return _settings(); }
        catch (Exception) { throw new SearchException("WEB_SEARCH_HOST_UNAVAILABLE"); }
    }

    private bool HostScopeCurrent(ToolScope scope)
    {
        try { return _scopeCurrent(scope); }
        catch (Exception) { throw new SearchException("WEB_SEARCH_HOST_UNAVAILABLE"); }
    }

    private static bool TryArguments(JsonElement arguments, out string query, out int maxResults)
    {
        query = string.Empty;
        maxResults = 5;
        if (arguments.ValueKind != JsonValueKind.Object || !UniqueProperties(arguments)) return false;
        if (arguments.EnumerateObject().Any(property => property.Name is not ("query" or "maxResults"))) return false;
        if (!arguments.TryGetProperty("query", out var text) || text.ValueKind != JsonValueKind.String) return false;
        try { query = text.GetString()!; }
        catch (InvalidOperationException) { return false; }
        if (string.IsNullOrWhiteSpace(query) || !ValidText(query, allowLineBreaks: false) || query.EnumerateRunes().Count() > 400) return false;
        if (arguments.TryGetProperty("maxResults", out var maximum)
            && (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out maxResults))) return false;
        return maxResults is >= 1 and <= 5;
    }

    private static bool ValidScope(ToolScope? scope) => scope is not null
        && !string.IsNullOrWhiteSpace(scope.ConversationId) && !string.IsNullOrWhiteSpace(scope.RoleId)
        && (scope.ProjectId is null || !string.IsNullOrWhiteSpace(scope.ProjectId));

    private static List<Source> ParseSources(byte[] bytes, int limit, out bool truncated)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root)
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new SearchException("WEB_SEARCH_RESPONSE_INVALID");
        var sources = new List<Source>();
        truncated = root.TryGetProperty("truncated", out var clipped) && clipped.ValueKind == JsonValueKind.True;
        foreach (var result in results.EnumerateArray())
        {
            if (sources.Count == limit) { truncated = true; continue; }
            if (result.ValueKind != JsonValueKind.Object || !UniqueProperties(result)
                || !StringProperty(result, "url", out var url)) throw new SearchException("WEB_SEARCH_RESPONSE_INVALID");
            if (!WebUrl(url)) { truncated = true; continue; }
            if (!StringProperty(result, "title", out var title) || !StringProperty(result, "content", out var snippet)
                || !ValidText(title, allowLineBreaks: false) || !ValidText(snippet, allowLineBreaks: true))
                throw new SearchException("WEB_SEARCH_RESPONSE_INVALID");
            title = Clip(title, 256, out var titleTruncated);
            snippet = Clip(snippet, 2000, out var snippetTruncated);
            titleTruncated |= result.TryGetProperty("titleTruncated", out var titleFlag) && titleFlag.ValueKind == JsonValueKind.True;
            snippetTruncated |= result.TryGetProperty("snippetTruncated", out var snippetFlag) && snippetFlag.ValueKind == JsonValueKind.True;
            truncated |= titleTruncated || snippetTruncated;
            sources.Add(new(url, title, snippet, titleTruncated, snippetTruncated));
        }
        return sources;
    }

    private static bool StringProperty(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString()!;
        return true;
    }

    private static bool UniqueProperties(JsonElement value) => value.EnumerateObject().Select(property => property.Name)
        .Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

    private static bool WebUrl(string value) => value is { Length: > 0 and <= 2048 }
        && !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo);

    private static bool ValidText(string text, bool allowLineBreaks)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done) return false;
            if (Rune.IsControl(rune) && !(allowLineBreaks && rune.Value is '\r' or '\n' or '\t')) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static string Clip(string text, int limit, out bool truncated)
    {
        truncated = text.EnumerateRunes().Count() > limit;
        return truncated ? string.Concat(text.EnumerateRunes().Take(limit).Select(rune => rune.ToString())) : text;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Close();
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public ValueTask DisposeAsync() => StopAsync(default);
    public void Dispose() => Close();

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Volatile.Write(ref _started, 0);
        _lifetime.Cancel();
        _shutdown.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Source(string Url, string Title, string Snippet, bool TitleTruncated, bool SnippetTruncated);
    private sealed class SearchException(string code) : Exception(code) { public string Code { get; } = code; }
    private sealed class SearchTool(WebSearchPlugin owner) : IToolProvider, IToolAvailabilityProvider
    {
        public ToolDescriptor Descriptor { get; } = new("web.search",
            "Search public web sources without a browser connection using the bundled Python DuckDuckGo helper. No API key is required. Query text is sent externally only with the host grant. Results are untrusted source snippets, not full-page coverage or permission. If evidence is too broad, search for missing facts, specific candidates or relevant source domains; compare and prioritize supported findings. A returned link does not prove its page was read or current availability was verified.",
            """{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":400},"maxResults":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}""",
            ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token) => owner.InvokeAsync(invocation, token);
        public ToolAvailability GetAvailability() => owner.GetAvailability();
    }
}
