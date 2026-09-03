using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.Core.Tests.Agents;

public sealed class AgentDiagnosticSummaryTests
{
    [Fact]
    public void Diagnostic_contains_counts_but_no_paths_or_target_ids()
    {
        var snapshot = new AgentRelaySnapshot(
            AgentRelayConnectionState.Connected, true, true, true,
            DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [],
            [new AgentApprovedSource("codex", "instance-secret", "Codex", "1", true,
                ["target-secret", @"C:\private\project"], true)],
            "relay_offline");
        var catalog = new AgentTargetCatalogResult(
            AgentTargetCatalogStatus.Available,
            [new AgentTargetDescriptor("target-secret", "Project", false)],
            "relay_offline");

        var text = AgentDiagnosticSummary.Build(snapshot, catalog,
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("protocol_version=1", text, StringComparison.Ordinal);
        Assert.Contains("Connected", text, StringComparison.Ordinal);
        Assert.Contains("target_count=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("target-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private\project", text, StringComparison.Ordinal);
        Assert.DoesNotContain("instance-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("relay_offline", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_catalog_produces_safe_zero_counts()
    {
        var text = AgentDiagnosticSummary.Build(
            AgentRelaySnapshot.Disabled,
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_query_failed"),
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("target_count=0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("adapter_query_failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_offline_state_takes_precedence_over_safe_error_text()
    {
        var text = AgentDiagnosticSummary.Build(
            new AgentRelaySnapshot(
                AgentRelayConnectionState.RelayOffline, false, false, false,
                DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [], [], "relay_offline"),
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.Available, []),
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("safe_error=relay_offline", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_mismatch_state_takes_precedence_over_safe_error_text()
    {
        var text = AgentDiagnosticSummary.Build(
            new AgentRelaySnapshot(
                AgentRelayConnectionState.VersionMismatch, true, false, false,
                DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [], [], "version_mismatch"),
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.Available, []),
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("safe_error=version_mismatch", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_offline_state_maps_to_adapter_unavailable_without_error()
    {
        var text = AgentDiagnosticSummary.Build(
            new AgentRelaySnapshot(
                AgentRelayConnectionState.AdapterOffline, true, true, false,
                DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [], []),
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.Available, []),
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("safe_error=adapter_unavailable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_failed_state_maps_to_unknown_error_without_error()
    {
        var text = AgentDiagnosticSummary.Build(
            new AgentRelaySnapshot(
                AgentRelayConnectionState.AuthenticationFailed, true, true, true,
                DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [], []),
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.Available, []),
            DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

        Assert.Contains("safe_error=unknown_error", text, StringComparison.Ordinal);
    }
}
