using System.Windows.Threading;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>A separate input/session owner; never changes the desktop model or its composer.</summary>
internal sealed class IndependentConversationGateway : IIndependentConversationGateway, IDisposable
{
    private readonly ConversationViewModel desktop;
    private readonly ConversationOrchestrator source;
    private ConversationOrchestrator session;
    private ConversationViewModel model;
    private readonly IDialogueSettingsStore settings;
    private readonly IndependentConversationScope authority;
    private readonly IndependentConversationImages images;
    private readonly Dispatcher dispatcher;
    private bool busy;
    private bool disposed;
    private string? sessionId;
    private readonly TimeProvider clock;
    internal const string ScopeMarker = "qq-independent";

    internal IndependentConversationGateway(ConversationViewModel desktop, ConversationOrchestrator source,
        IDialogueSettingsStore settings, IndependentConversationScope authority, IndependentConversationImages images,
        Dispatcher dispatcher, TimeProvider clock)
    {
        this.desktop = desktop; this.source = source; this.settings = settings; this.authority = authority; this.images = images;
        this.dispatcher = dispatcher;
        this.clock = clock;
        session = source.CreateIndependentSession("Qq", authority.IsActive);
        model = new(session, settings, time: clock);
    }

    public Task<ExternalConversationBinding?> CaptureCurrentScopeAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.FromResult(string.IsNullOrWhiteSpace(desktop.ActiveServantId) ? null :
            new ExternalConversationBinding(desktop.ActiveServantId, ScopeMarker,
                string.IsNullOrWhiteSpace(desktop.SessionContext.ProjectId) ? null : desktop.SessionContext.ProjectId)), cancellationToken);

    public Task<ExternalConversationResult> ExchangeAsync(ExternalConversationBinding scope, string text,
        ExternalConversationImage? image = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => ExchangeCoreAsync(scope, text, image, cancellationToken), cancellationToken);

    private async Task<ExternalConversationResult> ExchangeCoreAsync(ExternalConversationBinding scope, string text,
        ExternalConversationImage? image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed) return Failure("CHAT_EXTERNAL_CLOSED");
        if (busy) return Failure("CHAT_EXTERNAL_BUSY");
        if (!scope.ConversationId.StartsWith("qq-session-", StringComparison.Ordinal) || scope.ConversationId.Length != 43 ||
            !scope.ConversationId.AsSpan(11).ToArray().All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(scope.RoleId) || text.Length > 8192)
            return Failure("CHAT_EXTERNAL_INVALID_TEXT");
        busy = true;
        try
        {
            if (sessionId != scope.ConversationId)
            {
                model.Dispose();
                session.CloseIndependentSession();
                session = source.CreateIndependentSession("Qq" + scope.ConversationId[11..], authority.IsActive);
                model = new(session, settings, time: clock);
                sessionId = scope.ConversationId;
            }
            if (settings.Load().ModelConnection is not { } connection) return Failure("CHAT_EXTERNAL_CONFIGURATION_REQUIRED");
            if (model.IsConfigurationRequired && !model.HasPendingRun)
            {
                model.Dispose();
                model = new(session, settings, time: clock);
            }
            if (text.Trim() == "/新对话" && image is null)
            {
                if (model.HasPendingRun) return new(ExternalConversationStatus.WaitingInput,
                    "请先回复 /取消 结束当前任务，再新建对话。");
                session.StartNewConversation(scope.RoleId);
                if (model.ActiveServantId == scope.RoleId) model.NewConversationCommand.Execute(null);
                return new(ExternalConversationStatus.Completed, "已开始新的 QQ 对话。上一段聊天仍保存在历史中。");
            }
            var route = ModelRouteKey.From(connection);
            ChatImageInput? decoded = null;
            if (image is not null)
            {
                if (!ImageModelCapabilities.Supports(route) || connection.ToolsSupported == false)
                    return new(ExternalConversationStatus.Unavailable, "当前模型连接不支持图片输入，请在电脑上切换支持读图的模型。");
                if (image.ContentType is not ("image/png" or "image/jpeg")) return Failure("IMAGE_TYPE_UNSUPPORTED");
                decoded = SelectedImageDecoder.Decode(image.Bytes, image.ContentType == "image/png" ? ".png" : ".jpg", token);
            }
            var id = await session.PrepareConversationAsync(scope.RoleId, scope.ProjectId, token);
            if (model.ActiveServantId != scope.RoleId) model.SetActiveServant(scope.RoleId);
            if (model.CurrentConversationId != id) model.SetActiveConversation(id);
            var binding = new ExternalConversationBinding(scope.RoleId, id, scope.ProjectId);
            using var lease = authority.Enter(new(id, scope.RoleId, scope.ProjectId));
            if (image is not null && model.HasPendingRun)
                return new(ExternalConversationStatus.WaitingInput, "请先用文字回答或回复 /取消，再发送图片。");
            void OnPersisted(ConversationUpdate update)
            {
                if (decoded is not null && update.Type == ConversationUpdateType.UserMessagePersisted &&
                    update.ConversationId == id && update.ServantId == scope.RoleId && update.MessageId is { } userId)
                    images.Store.Grant(new(id, scope.RoleId, scope.ProjectId), route, decoded, userId);
            }
            session.Updated += OnPersisted;
            try
            {
                var result = await model.ExchangeExternalTextAsync(binding,
                    string.IsNullOrWhiteSpace(text) && image is not null ? "请查看这张图片。" : text, token);
                // A hidden remote view must never claim that an invisible approval can be completed locally.
                // Cancel the proposed action, then ask the user to initiate it on the native desktop surface.
                if (result.Status == ExternalConversationStatus.WaitingApproval && model.PendingApproval is { } approval)
                {
                    if (!await session.CancelAgentRunAsync(approval.Binding.Identity.RunId, new(id, scope.RoleId, scope.ProjectId), token))
                        return Failure("CHAT_EXTERNAL_CANCEL_NOT_CONFIRMED");
                    model.SetActiveConversation(id);
                    return new(ExternalConversationStatus.Cancelled, "这项操作需要桌面确认，QQ 中未执行。请在桌面聊天中重新发起并确认。");
                }
                if (result.Input?.Questions.Count > 1)
                    return result with { Text = "这次需要回答多个问题，QQ 入口暂不支持。请回复 /取消 结束本次提问，再在桌面聊天中重新发起。" };
                return result;
            }
            finally { session.Updated -= OnPersisted; }
        }
        catch (SelectedImageException) { return new(ExternalConversationStatus.InvalidInput, "图片无法读取，请发送单张有效的 PNG/JPEG（不超过 8 MiB、400 万像素）。"); }
        finally { if (disposed || !model.HasPendingRun) images.Store.Clear(); busy = false; }
    }

    private static ExternalConversationResult Failure(string code) => new(ExternalConversationStatus.Unavailable, ErrorCode: code);
    private Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return Task.FromCanceled<T>(new(true));
        return dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task.Unwrap();
    }
    public void Dispose() { disposed = true; session.CloseIndependentSession(); images.Store.Clear(); model.Dispose(); }
}
