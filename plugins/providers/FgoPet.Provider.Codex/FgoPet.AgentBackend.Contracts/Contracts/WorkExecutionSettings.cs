using FgoPet.Core.Agents;

namespace FgoPet.Work.Execution.Settings;

public sealed record WorkExecutionSettings(AgentConnectionSettings AgentConnection)
{
    public static WorkExecutionSettings Defaults { get; } = new(AgentConnectionSettings.Defaults);
}

public interface IWorkExecutionSettingsStore
{
    WorkExecutionSettings Load();
    void Save(WorkExecutionSettings settings);
}
