using System.Collections.Immutable;

namespace FgoPet.Architecture.Tests;

internal sealed record LegacyCompileDebt(string Project, string Source);
internal sealed record DependencyDebt(string From, string To, string Kind);
internal sealed record MigrationProject(string Path, ImmutableArray<string> LinkedSources, ImmutableArray<DependencyDebt> References, bool IsTestProject = false);
internal sealed record MigrationBaseline(int SchemaVersion, ImmutableArray<LegacyCompileDebt> LegacyCompileItems,
    ImmutableArray<DependencyDebt> DependencyExceptions)
{
    internal static MigrationBaseline Empty { get; } = new(1, [], []);
}

/// <summary>Desired rules are explicit; observed debt is a separate, shrinking set of exact identities.</summary>
internal static class MigrationBoundaryGate
{
    internal static ImmutableArray<string> Verify(ArchitecturePolicy policy, MigrationBaseline baseline,
        MigrationBaseline original, IEnumerable<MigrationProject> evaluated)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        var projects = policy.Projects.ToDictionary(project => project.Path, StringComparer.OrdinalIgnoreCase);
        var snapshots = evaluated.ToArray();
        var paths = snapshots.Select(project => project.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalCompiles = original.LegacyCompileItems.Select(CompileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalEdges = original.DependencyExceptions.Select(EdgeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compiles = baseline.LegacyCompileItems.Select(CompileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = baseline.DependencyExceptions.Select(EdgeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in compiles.Except(originalCompiles, StringComparer.OrdinalIgnoreCase)) errors.Add("baseline grew: legacy compile " + key);
        foreach (var key in edges.Except(originalEdges, StringComparer.OrdinalIgnoreCase)) errors.Add("baseline grew: dependency " + key);
        foreach (var project in projects.Values)
            if (!paths.Contains(project.Path)) errors.Add("missing project: " + project.Path);

        foreach (var snapshot in snapshots)
        {
            if (!projects.TryGetValue(snapshot.Path, out var from))
            {
                errors.Add("unclassified project: " + snapshot.Path);
                continue;
            }
            if ((from.Role == ArchitectureRole.Test) != snapshot.IsTestProject)
                errors.Add("test classification mismatch: " + snapshot.Path);
            if (from.Module == "bootstrap")
            {
                var ownedDirectory = snapshot.Path[..(snapshot.Path.LastIndexOf('/') + 1)];
                foreach (var source in snapshot.LinkedSources)
                    if (!source.StartsWith(ownedDirectory, StringComparison.OrdinalIgnoreCase))
                        errors.Add("foreign bootstrap compile: " + snapshot.Path + " -> " + source);
            }
            if (from.Module.StartsWith("legacy-", StringComparison.Ordinal)
                && from.Module is "legacy-core" or "legacy-infrastructure" or "legacy-app")
            {
                foreach (var source in snapshot.LinkedSources)
                    if (!compiles.Contains(CompileKey(new(snapshot.Path, source)))) errors.Add("new legacy compile: " + snapshot.Path + " -> " + source);
            }
            foreach (var reference in snapshot.References)
            {
                if (!projects.TryGetValue(reference.To, out var to))
                {
                    errors.Add("unclassified dependency target: " + reference.To);
                    continue;
                }
                var kind = reference.Kind == "compile" ? ArchitectureDependencyKind.Compile : ArchitectureDependencyKind.Companion;
                if (!Allows(policy, from, to, kind) && !edges.Contains(EdgeKey(reference)))
                    errors.Add("forbidden dependency: " + EdgeKey(reference));
            }
        }
        return errors.ToImmutable();
    }

    internal static bool Allows(ArchitecturePolicy policy, ArchitectureProject from, ArchitectureProject to, ArchitectureDependencyKind kind) =>
        from.Role == ArchitectureRole.Test
        || string.Equals(from.Module, to.Module, StringComparison.Ordinal)
        || policy.DependencyRules.Any(rule => rule.FromModules.Contains(from.Module) && rule.FromLayers.Contains(from.Layer)
            && rule.ToModules.Contains(to.Module) && rule.ToLayers.Contains(to.Layer) && rule.Kinds.Contains(kind));

    // Directory moves retain debt identity; the immutable origin and shrinking history stay authoritative.
    // Only these exact project relocations are aliases. Current policy and evaluated paths are never rewritten.
    private static readonly IReadOnlyDictionary<string, string> OriginalProjectPaths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["src/FgoPet.Desktop/DataManagement/FgoPet.DataManagement.csproj"] = "host/DataManagement/src/FgoPet.DataManagement/FgoPet.DataManagement.csproj",
        ["src/FgoPet.Desktop/Shell/FgoPet.DesktopShell.csproj"] = "host/DesktopShell/src/FgoPet.DesktopShell/FgoPet.DesktopShell.csproj",
        ["src/FgoPet.Desktop/Contracts/FgoPet.HostContracts.csproj"] = "host/DesktopShell/src/FgoPet.HostContracts/FgoPet.HostContracts.csproj",
        ["src/FgoPet.Desktop/Settings/FgoPet.SettingsHost.csproj"] = "host/Settings/src/FgoPet.SettingsHost/FgoPet.SettingsHost.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentBackend.Contracts/FgoPet.AgentBackend.Contracts.csproj"] = "modules/agent-integration/src/FgoPet.AgentBackend.Contracts/FgoPet.AgentBackend.Contracts.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentBackend.Desktop/FgoPet.AgentBackend.Desktop.csproj"] = "modules/agent-integration/src/FgoPet.AgentBackend.Desktop/FgoPet.AgentBackend.Desktop.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentBackend.Windows/FgoPet.AgentBackend.Windows.csproj"] = "modules/agent-integration/src/FgoPet.AgentBackend.Windows/FgoPet.AgentBackend.Windows.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentProtocol/FgoPet.AgentProtocol.csproj"] = "modules/agent-integration/src/FgoPet.AgentProtocol/FgoPet.AgentProtocol.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentRelay/FgoPet.AgentRelay.csproj"] = "modules/agent-integration/src/FgoPet.AgentRelay/FgoPet.AgentRelay.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj"] = "modules/agent-integration/src/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj",
        ["plugins/providers/FgoPet.Provider.Codex/FgoPet.CodexAdapter/FgoPet.CodexAdapter.csproj"] = "modules/agent-integration/src/FgoPet.CodexAdapter/FgoPet.CodexAdapter.csproj",
        ["tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj"] = "modules/agent-integration/tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj",
        ["tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj"] = "modules/agent-integration/tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj",
        ["tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj"] = "modules/agent-integration/tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj",
        ["tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj"] = "modules/agent-integration/tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj",
        ["plugins/FgoPet.Plugin.Content/Desktop/FgoPet.Character.csproj"] = "modules/character/src/FgoPet.Character/FgoPet.Character.csproj",
        ["plugins/FgoPet.Plugin.Dialogue/FgoPet.Dialogue.csproj"] = "modules/dialogue/src/FgoPet.Dialogue/FgoPet.Dialogue.csproj",
        ["plugins/FgoPet.Plugin.Memory/Desktop/FgoPet.Memory.csproj"] = "modules/memory/src/FgoPet.Memory/FgoPet.Memory.csproj",
        ["plugins/FgoPet.Plugin.Speech/Contracts/FgoPet.Speech.Core.csproj"] = "modules/speech/src/FgoPet.Speech.Core/FgoPet.Speech.Core.csproj",
        ["plugins/FgoPet.Plugin.Speech/Desktop/FgoPet.Speech.Desktop.csproj"] = "modules/speech/src/FgoPet.Speech.Desktop/FgoPet.Speech.Desktop.csproj",
        ["plugins/FgoPet.Plugin.Speech/Infrastructure/FgoPet.Speech.Infrastructure.csproj"] = "modules/speech/src/FgoPet.Speech.Infrastructure/FgoPet.Speech.Infrastructure.csproj",
        ["tests/FgoPet.Speech.Core.Tests/FgoPet.Speech.Core.Tests.csproj"] = "modules/speech/tests/FgoPet.Speech.Core.Tests/FgoPet.Speech.Core.Tests.csproj",
        ["tests/FgoPet.Speech.Desktop.Tests/FgoPet.Speech.Desktop.Tests.csproj"] = "modules/speech/tests/FgoPet.Speech.Desktop.Tests/FgoPet.Speech.Desktop.Tests.csproj",
        ["tests/FgoPet.Speech.Infrastructure.Tests/FgoPet.Speech.Infrastructure.Tests.csproj"] = "modules/speech/tests/FgoPet.Speech.Infrastructure.Tests/FgoPet.Speech.Infrastructure.Tests.csproj",
        ["src/FgoPet.Platform/Contracts/FgoPet.Platform.Contracts.csproj"] = "platform/src/FgoPet.Platform.Contracts/FgoPet.Platform.Contracts.csproj",
        ["src/FgoPet.Platform/Storage/FgoPet.Platform.Storage.csproj"] = "platform/src/FgoPet.Platform.Storage/FgoPet.Platform.Storage.csproj",
        ["src/FgoPet.Platform/Windows/FgoPet.Platform.Windows.csproj"] = "platform/src/FgoPet.Platform.Windows/FgoPet.Platform.Windows.csproj",
    };

    private static string OriginalPath(string path) => OriginalProjectPaths.TryGetValue(path, out var original) ? original : path;
    private static string CompileKey(LegacyCompileDebt item) => OriginalPath(item.Project) + " -> " + item.Source;
    private static string EdgeKey(DependencyDebt item) => OriginalPath(item.From) + " -> " + OriginalPath(item.To) + " (" + item.Kind + ")";
}
