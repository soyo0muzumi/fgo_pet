using FgoPet.Extensibility;

namespace FgoPet.Plugin.Workspace;

/// <summary>In-process host grants only. No model tool or package can create or change a grant.</summary>
public sealed class WorkspaceAuthorizationRegistry(Func<ToolScope, bool> isCurrent) : IWorkspaceAuthorizationSource
{
    private readonly object gate = new();
    private readonly Dictionary<ToolScope, WorkspaceAuthorization> grants = [];
    private long revision;
    public WorkspaceAuthorization Grant(ToolScope scope, string rootPath, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!isCurrent(scope) || !Path.IsPathFullyQualified(rootPath) || !Path.IsPathFullyQualified(currentDirectory))
            throw new WorkspaceAccessException("WORKSPACE_SCOPE_DENIED");
        lock (gate)
        {
            if (!grants.ContainsKey(scope) && grants.Count >= 64) throw new WorkspaceAccessException("WORKSPACE_UNAVAILABLE");
            var grant = new WorkspaceAuthorization(scope, new("workspace-" + Guid.NewGuid().ToString("N"), checked(++revision)),
                rootPath, currentDirectory);
            grants[scope] = grant;
            return grant;
        }
    }
    public void Revoke(ToolScope scope) { lock (gate) { grants.Remove(scope); } }
    public WorkspaceAuthorization? GetCurrent(ToolScope scope)
    {
        if (!isCurrent(scope)) return null;
        lock (gate) return grants.GetValueOrDefault(scope);
    }
}
