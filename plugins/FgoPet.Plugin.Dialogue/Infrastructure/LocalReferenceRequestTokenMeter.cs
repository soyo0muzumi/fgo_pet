using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;

namespace FgoPet.Infrastructure.Providers;

/// <summary>Metadata from a host-owned, pinned local counter; never provider usage.</summary>
public sealed record LocalReferenceTokenCount(int InputTokens, string InputHash, string WireHash, string PolicyHash);

public interface ILocalReferenceTokenCounter
{
    LocalReferenceTokenCount? TryCount(string input, string wire);
}

/// <summary>Explicitly composed reference estimation. Product defaults remain conservative.</summary>
public sealed class LocalReferenceRequestTokenMeter : IRequestTokenMeter
{
    private readonly RequestTokenMeter _conservative = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TokenMeasurement> _cache = new(StringComparer.Ordinal);
    private readonly ModelRouteKey _approvedRoute;
    private readonly string _policyHash;
    private readonly ILocalReferenceTokenCounter _counter;
    private static readonly HashSet<string> OfficialEndpoints = new(StringComparer.Ordinal)
    {
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com", "deepseek-flash")).EndpointKey,
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com/v1", "deepseek-flash")).EndpointKey
    };

    public LocalReferenceRequestTokenMeter(ModelRouteKey approvedRoute, string expectedPolicyHash, ILocalReferenceTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(approvedRoute);
        ArgumentNullException.ThrowIfNull(expectedPolicyHash);
        ArgumentNullException.ThrowIfNull(counter);
        _approvedRoute = approvedRoute; _policyHash = expectedPolicyHash; _counter = counter;
    }

    public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
    {
        var fallback = _conservative.Measure(route, request);
        // This reference counter handles text only. Images retain the conservative image reserve
        // (plus the ordinary envelope estimate); base64 text counting is not image token counting.
        if (fallback.Kind == TokenCountKind.ProviderUsage || ChatImageInputs.HasImages(request)
            || route != _approvedRoute || route.ProviderId != "deepseek"
            || route.ModelId != "deepseek-flash" || !OfficialEndpoints.Contains(route.EndpointKey) || !IsHash(_policyHash)
            || request.MaxOutputTokens != 2048 || request.Metadata.Count != 0
            || request.Tools is { Count: > 0 } && request.ToolChoice is not (null or "auto")) return fallback;
        lock (_gate)
        {
            if (_cache.TryGetValue(fallback.Fingerprint, out var cached)) return cached;
            var input = ChatRequestInputEnvelope.Write(route.ModelId, request);
            var wire = ChatRequestPayloadWriter.Write(route, request);
            if (Encoding.UTF8.GetByteCount(input) + Encoding.UTF8.GetByteCount(wire) > 256 * 1024) return fallback;
            LocalReferenceTokenCount? count;
            try { count = _counter.TryCount(input, wire); }
            catch (Exception error) when (error is IOException or TimeoutException or JsonException) { return fallback; }
            if (count is null || count.InputTokens <= 0
                || !StringComparer.OrdinalIgnoreCase.Equals(count.PolicyHash, _policyHash)
                || !StringComparer.OrdinalIgnoreCase.Equals(count.InputHash, Hash(input))
                || !StringComparer.OrdinalIgnoreCase.Equals(count.WireHash, Hash(wire))) return fallback;
            var measured = new TokenMeasurement(count.InputTokens, TokenCountKind.LocalReferenceEstimate, fallback.Fingerprint)
                { ReferencePolicyHash = _policyHash.ToUpperInvariant() };
            if (_cache.Count >= 64) _cache.Clear();
            _cache[fallback.Fingerprint] = measured;
            return measured;
        }
    }
    public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) => _conservative.RecordUsage(route, request, usage);
    public void Invalidate(ModelRouteKey route)
    {
        lock (_gate)
        {
            _conservative.Invalidate(route);
            if (route == _approvedRoute) _cache.Clear();
        }
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
