using FgoPet.UiSdk;

namespace FgoPet.Dialogue.Contracts;

/// <summary>Safe role and selector metadata supplied by the native host.</summary>
public sealed record ChatWebHostPresentation(
    string RoleName,
    string? AvatarPngDataUrl,
    IReadOnlyList<ChatWebChoice> Projects,
    IReadOnlyList<ChatWebChoice> Models,
    bool IsProjectsLoading = false,
    string ProjectsStatus = "",
    ChatWebHostAvailability? Actions = null);

/// <summary>A value selected from a current, host-owned catalog.</summary>
public sealed record ChatWebChoice(string Id, string Label, bool Selected, bool CanSelect = true);

/// <summary>Current availability of the finite native navigation and refresh actions.</summary>
public sealed record ChatWebHostAvailability(
    bool CanOpenFocus = false,
    bool CanOpenWorkspaceOverview = false,
    bool CanCreateWorkspaceItem = false,
    bool CanOpenPersonalizationSettings = false,
    bool CanOpenSpeechSettings = false,
    bool CanOpenModelSettings = false,
    bool CanRefreshProjects = false);

public enum ChatWebHostAction
{
    Hide,
    SetExpanded,
    CopyTurn,
    ReadTurn,
    OpenSource,
    OpenWorkspace,
    OpenFocus,
    OpenWorkspaceOverview,
    NewWorkspaceItem,
    OpenPersonalizationSettings,
    OpenSpeechSettings,
    OpenModelSettings,
    RefreshProjects,
    SelectProject,
    SelectModel,
}

/// <summary>Finite request passed from the Dialogue adapter to its native host.</summary>
public sealed record ChatWebHostRequest(
    ChatWebHostAction Action,
    string? TargetId = null,
    bool? Expanded = null);

/// <summary>Port for role, selector, availability, refresh, and native presentation actions.</summary>
public interface IChatWebHostActions
{
    event EventHandler? Changed;

    ChatWebHostPresentation ReadPresentation();

    ValueTask<WebSurfaceCommandResult> HandleAsync(
        ChatWebHostRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Session-scoped projection over the existing ConversationViewModel.</summary>
public interface IChatWebSession : IDisposable
{
    ChatWebSnapshot ReadSnapshot();

    ValueTask<WebSurfaceCommandResult> HandleCommandAsync(
        WebSurfaceMessage message,
        CancellationToken cancellationToken);

    event Action<ChatWebSnapshot>? Changed;
}

/// <summary>Creates a lightweight adapter over the application's shared conversation.</summary>
public interface IChatWebSessionFactory
{
    IChatWebSession Create(IChatWebHostActions host);
}
