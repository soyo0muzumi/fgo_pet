namespace FgoPet.Core.Agents;

public sealed record AgentConnectionSettings
{
    public AgentConnectionSettings(
        bool Enabled = false,
        IReadOnlyDictionary<string, bool>? SourceEnabled = null,
        IReadOnlyDictionary<string, IReadOnlyList<AgentProjectTarget>>? ProjectAllowlist = null)
    {
        this.Enabled = Enabled;
        this.SourceEnabled = SourceEnabled is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(SourceEnabled, StringComparer.Ordinal);
        this.ProjectAllowlist = ProjectAllowlist is null
            ? new Dictionary<string, IReadOnlyList<AgentProjectTarget>>(StringComparer.Ordinal)
            : ProjectAllowlist.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AgentProjectTarget>)pair.Value.ToArray(),
                StringComparer.Ordinal);
    }

    public bool Enabled { get; }
    public bool IsEnabled => Enabled;
    public IReadOnlyDictionary<string, bool> SourceEnabled { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<AgentProjectTarget>> ProjectAllowlist { get; }

    public bool IsSourceEnabled(string sourceType)
    {
        return Enabled && SourceEnabled.TryGetValue(sourceType, out var sourceEnabled) && sourceEnabled;
    }

    public bool IsTargetAllowed(string sourceType, string targetId)
    {
        return IsSourceEnabled(sourceType)
            && ProjectAllowlist.TryGetValue(sourceType, out var targets)
            && targets.Any(target => string.Equals(target.TargetId, targetId, StringComparison.Ordinal));
    }

    public static AgentConnectionSettings Defaults { get; } = new();
}
