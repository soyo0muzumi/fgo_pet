namespace FgoPet.Extensibility;

/// <summary>Host-issued resource authority; never constructed from model arguments.</summary>
public sealed record ToolResourceAuthorization(string Id, long Revision)
{
    public string? Fingerprint { get; init; }
}
public interface IToolResourceAuthorizationProvider
{
    ToolResourceAuthorization GetAuthorization(ToolScope scope);
}

public sealed record WorkspaceAuthorization(ToolScope Scope, ToolResourceAuthorization Authorization,
    string RootPath, string CurrentDirectory);
public interface IWorkspaceAuthorizationSource
{
    WorkspaceAuthorization? GetCurrent(ToolScope scope);
}
public enum WorkspacePathKind { File, Directory }
public interface IWorkspacePathLease : IDisposable
{
    WorkspaceAuthorization Authority { get; }
    string FullPath { get; }
    bool Exists { get; }
    void Revalidate();
    void ValidateOpenedFile(FileStream file);
}
public interface IWorkspaceAccessGuard
{
    WorkspaceAuthorization GetAuthorization(ToolScope scope);
    IWorkspacePathLease Open(ToolScope scope, string path, WorkspacePathKind kind, bool allowCreate = false);
}
public sealed class WorkspaceAccessException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
