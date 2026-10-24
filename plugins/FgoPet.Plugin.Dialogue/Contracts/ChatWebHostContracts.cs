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
    ChatWebHostAvailability? Actions = null,
    string? WorkspaceLabel = null,
    string? ImageLabel = null,
    string ImageDisclosure = "",
    bool IsModelsLoading = false,
    string ModelsStatus = "",
    ChatWebWindowPresentation? Window = null,
    ChatWebWindowContext? WindowContext = null);

/// <summary>Metadata only. Local preview owns the text; no native token is exposed.</summary>
public sealed record ChatWebWindowContext(string SourceTitle, string Application,
    DateTimeOffset ReadAtUtc, string Status, int CharacterCount);

/// <summary>Native-owned presentation. ComposerHeight excludes the 22 DIP drag strip.</summary>
public sealed record ChatWebWindowPresentation(string View, bool Wide, double ComposerHeight = 160);

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
    bool CanRefreshProjects = false,
    bool CanChooseWorkspace = false,
    bool CanClearWorkspace = false,
    bool CanChooseImage = false,
    bool CanClearImage = false,
    bool CanConfigureBrowserImages = false,
    bool CanChooseWindowContext = false,
    bool CanPreviewWindowContext = false,
    bool CanRereadWindowContext = false,
    bool CanClearWindowContext = false);

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
    ChooseWorkspace,
    ClearWorkspace,
    ChooseImage,
    ClearImage,
    ConfigureBrowserImages,
    RefreshModels,
    SetPresentation,
    ChooseWindowContext,
    PreviewWindowContext,
    RereadWindowContext,
    ClearWindowContext,
}

/// <summary>Finite request passed from the Dialogue adapter to its native host.</summary>
public sealed record ChatWebHostRequest(
    ChatWebHostAction Action,
    string? TargetId = null,
    bool? Expanded = null,
    string? View = null,
    bool? Wide = null,
    double? ComposerHeight = null);

/// <summary>Port for role, selector, availability, refresh, and native presentation actions.</summary>
public interface IChatWebHostActions
{
    event EventHandler? Changed;

    ChatWebHostPresentation ReadPresentation();

    ValueTask<WebSurfaceCommandResult> ImportImageAsync(byte[] bytes, string mimeType, CancellationToken token) =>
        ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "CHAT_NOT_AVAILABLE"));

    /// <summary>Only a trusted native dialog may return Allow. Hosts without this UI fail closed.</summary>
    ValueTask<ChatApprovalDecision> ReviewApprovalAsync(ChatWebApproval approval, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ChatApprovalDecision.Cancel);

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
