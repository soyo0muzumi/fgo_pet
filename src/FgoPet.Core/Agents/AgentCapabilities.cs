namespace FgoPet.Core.Agents;

public enum OpenMode
{
    Exact,
    AppOnly,
    None,
}

public sealed record AgentProjectTarget(string TargetId, string DisplayName)
{
    public string TargetId { get; } = AgentIdentityValidation.Id(TargetId, nameof(TargetId));
    public string DisplayName { get; } = AgentIdentityValidation.Id(DisplayName, nameof(DisplayName), 256);
}

public sealed record AgentCapabilities
{
    public AgentCapabilities(
        bool canCreateTask,
        bool canOpenTask,
        OpenMode openMode,
        IReadOnlyList<AgentProjectTarget>? projectTargets = null)
    {
        if (!canOpenTask && openMode != OpenMode.None)
        {
            throw new ArgumentException("An adapter that cannot open tasks must use OpenMode.None.", nameof(openMode));
        }

        if (canOpenTask && openMode == OpenMode.None)
        {
            throw new ArgumentException("An adapter that can open tasks must declare an open mode.", nameof(openMode));
        }

        CanCreateTask = canCreateTask;
        CanOpenTask = canOpenTask;
        OpenMode = openMode;
        ProjectTargets = projectTargets is null ? Array.Empty<AgentProjectTarget>() : projectTargets.ToArray();
    }

    public bool CanCreateTask { get; }
    public bool CanOpenTask { get; }
    public OpenMode OpenMode { get; }
    public IReadOnlyList<AgentProjectTarget> ProjectTargets { get; }
}
