using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FgoPet.Core.Agents;

public static class AgentDiagnosticSummary
{
    private const string ProtocolVersion = "1";
    private const int SourceInstanceDigestLength = 12;

    public static string Build(
        AgentRelaySnapshot snapshot,
        AgentTargetCatalogResult catalog,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        var sourceCount = snapshot.Sources.Count;
        var selectedCount = snapshot.Sources.Sum(source => source.AllowedTargetIds.Count);
        var targetCount = catalog.Targets.Count;
        var readOnlyCount = catalog.Targets.Count(target => target.IsReadOnly);

        return string.Join('\n',
            $"protocol_version={ProtocolVersion}",
            $"connection_state={snapshot.State}",
            $"relay_online={Bool(snapshot.RelayOnline)}",
            $"app_online={Bool(snapshot.AppOnline)}",
            $"adapter_online={Bool(snapshot.AdapterOnline)}",
            $"source_count={sourceCount.ToString(CultureInfo.InvariantCulture)}",
            $"source_instance_hashes={SourceInstanceHashes(snapshot.Sources)}",
            $"target_count={targetCount.ToString(CultureInfo.InvariantCulture)}",
            $"selected_count={selectedCount.ToString(CultureInfo.InvariantCulture)}",
            $"read_only_count={readOnlyCount.ToString(CultureInfo.InvariantCulture)}",
            $"catalog_status={catalog.Status}",
            $"safe_error={SafeErrorCategory(snapshot, catalog)}",
            $"observed_at_utc={observedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}");
    }

    private static string SourceInstanceHashes(IReadOnlyList<AgentApprovedSource> sources)
    {
        if (sources.Count == 0) return "none";

        return string.Join(',', sources.Select(source => ShortHash(source.SourceInstanceId)));
    }

    private static string ShortHash(string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return digest[..SourceInstanceDigestLength];
    }

    private static string SafeErrorCategory(AgentRelaySnapshot snapshot, AgentTargetCatalogResult catalog)
    {
        if (catalog.Status != AgentTargetCatalogStatus.Available)
        {
            return catalog.Status switch
            {
                AgentTargetCatalogStatus.AdapterNotInstalled => "adapter_not_installed",
                AgentTargetCatalogStatus.AdapterUnavailable => "adapter_unavailable",
                AgentTargetCatalogStatus.TimedOut => "adapter_timeout",
                AgentTargetCatalogStatus.InvalidResponse => "adapter_invalid_response",
                _ => "unknown_error",
            };
        }

        if (!string.IsNullOrWhiteSpace(snapshot.SafeError)
            || !string.IsNullOrWhiteSpace(catalog.SafeError))
        {
            return "unknown_error";
        }

        return snapshot.State switch
        {
            AgentRelayConnectionState.RelayOffline => "relay_offline",
            AgentRelayConnectionState.VersionMismatch => "version_mismatch",
            _ => "none",
        };
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
