namespace FgoPet.Kernel.Agent;

public sealed record RunBudget
{
    public RunBudget(int maxModelRequests = 8, int maxToolCalls = 16, int maxLoadedSkills = 8)
    {
        if (maxModelRequests <= 0 || maxToolCalls <= 0 || maxLoadedSkills <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxModelRequests));
        MaxModelRequests = maxModelRequests;
        MaxToolCalls = maxToolCalls;
        MaxLoadedSkills = maxLoadedSkills;
    }
    public int MaxModelRequests { get; }
    public int MaxToolCalls { get; }
    public int MaxLoadedSkills { get; }
}

public sealed class AgentBudgetExceededException(string dimension) : Exception("RUN_BUDGET_EXCEEDED")
{
    public string Dimension { get; } = dimension;
}

public sealed class AgentStateException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
