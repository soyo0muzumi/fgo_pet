using System.ComponentModel;
using System.Windows.Threading;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

internal sealed class ChatImageSelection : IChatImageSelection, IDisposable
{
    private readonly ConversationViewModel conversation;
    private readonly SelectedImageStore images;
    private readonly IDialogueSettingsStore settings;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer expiry;
    private ChatImageInput? pending;
    private ModelRouteKey? selectedRoute;
    private ToolScope? granted;
    private DateTimeOffset expiresAt;
    private long epoch;
    private bool disposed;
    private bool choosing;
    private bool wasStreaming;
    public ChatImageSelection(ConversationViewModel conversation, SelectedImageStore images,
        IDialogueSettingsStore settings, TimeProvider clock)
    {
        this.conversation = conversation; this.images = images; this.settings = settings; this.clock = clock;
        conversation.PropertyChanged += OnChanged;
        conversation.SessionChanged += Clear;
        conversation.RequestCancelled += OnRequestCancelled;
        conversation.DesktopMessagePersisted += Bind;
        expiry = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        expiry.Tick += OnExpiry;
    }
    private ModelRouteKey? Route => settings.Load().ModelConnection is { } connection ? ModelRouteKey.From(connection) : null;
    public bool CanChoose => !disposed && !choosing && !conversation.IsStreaming && !conversation.HasPendingRun
        && !string.IsNullOrWhiteSpace(conversation.ActiveServantId) && settings.Load().ModelConnection?.ToolsSupported != false && Route is { } route && ImageModelCapabilities.Supports(route);
    public string? Label => pending is null ? null : $"PNG · {pending.Width} × {pending.Height}";
    public string Disclosure
    {
        get
        {
            if (Route is not { } route || !ImageModelCapabilities.Supports(route))
                return "Image input requires a supported official OpenAI/DeepSeek connection or the exact local Buddy subscription route. Other routes are not enabled.";
            if (ImageModelCapabilities.IsLocalBuddySubscription(route))
                return $"Select one PNG/JPEG (up to 8 MiB / 4M pixels). Sending shares it with {route.ModelId} at Tencent CodeBuddy CN via local Buddy (127.0.0.1:8326). Attachments may be saved to disk and re-encoded. A run may send multiple requests. Buddy may retry or switch accounts; there is no shared upstream request cap. Stop/timeout only cancels the app request; images already sent cannot be recalled. Selection alone does not send.";
            return $"Select one PNG/JPEG (up to 8 MiB / 4M pixels). Sending shares it with {route.ModelId} at {(route.ProviderId == "deepseek" ? "DeepSeek" : "OpenAI")} for low-detail image analysis. Selection alone does not send.";
        }
    }
    public event EventHandler? Changed;
    public Task<bool> ImportAsync(byte[] bytes, string mimeType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanChoose || bytes.Length > SelectedImageFormat.MaxSourceBytes
            || mimeType is not ("image/png" or "image/jpeg")) return Task.FromResult(false);
        var originalEpoch = epoch;
        var scope = Scope();
        var route = Route!;
        try
        {
            var image = SelectedImageDecoder.Decode(bytes, mimeType == "image/png" ? ".png" : ".jpg", token);
            token.ThrowIfCancellationRequested();
            if (disposed || originalEpoch != epoch || scope != Scope() || route != Route
                || conversation.IsStreaming || conversation.HasPendingRun) return Task.FromResult(false);
            return Task.FromResult(Select(image, route));
        }
        catch (SelectedImageException) { return Task.FromResult(false); }
    }
    public async Task<bool> ChooseAsync(Func<string?> nativePicker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanChoose) return false;
        var originalEpoch = epoch;
        var scope = Scope();
        var route = Route!;
        choosing = true;
        try
        {
            var path = nativePicker();
            token.ThrowIfCancellationRequested();
            if (path is null) return true;
            var image = await SelectedImageDecoder.ReadAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (disposed || originalEpoch != epoch || scope != Scope() || route != Route
                || conversation.IsStreaming || conversation.HasPendingRun) return false;
            return Select(image, route);
        }
        catch (SelectedImageException) { return false; }
        finally { choosing = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private bool Select(ChatImageInput image, ModelRouteKey route)
    {
        // Publish only after replacement is complete. Changed handlers may invalidate
        // the session synchronously; never write the old image after that callback.
        epoch++; images.Clear(); granted = null;
        pending = image; selectedRoute = route; expiresAt = clock.GetUtcNow() + SelectedImageStore.Lifetime;
        expiry.Start(); Changed?.Invoke(this, EventArgs.Empty);
        return !disposed && ReferenceEquals(pending, image);
    }
    private ToolScope Scope() => new(conversation.CurrentConversationId, conversation.ActiveServantId,
        string.IsNullOrWhiteSpace(conversation.SessionContext.ProjectId) ? null : conversation.SessionContext.ProjectId);
    private void Bind(string userMessageId)
    {
        if (pending is null || granted is not null || string.IsNullOrWhiteSpace(conversation.CurrentConversationId)) return;
        if (Route != selectedRoute || clock.GetUtcNow() >= expiresAt) { Clear(); return; }
        try
        {
            images.Grant(Scope(), selectedRoute!, pending, userMessageId, expiresAt);
            granted = Scope();
        }
        catch (SelectedImageException) { Clear(); }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed) return;
        if (pending is not null)
        {
            if (Route != selectedRoute || clock.GetUtcNow() >= expiresAt
                || (granted is not null && wasStreaming && !conversation.IsStreaming && !conversation.HasPendingRun)) Clear();
        }
        wasStreaming = conversation.IsStreaming;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnExpiry(object? sender, EventArgs args)
    { if (clock.GetUtcNow() >= expiresAt || Route != selectedRoute) Clear(); }
    private void OnRequestCancelled()
    { if (granted is not null || !conversation.IsExternalRequest) Clear(); }
    public void Clear()
    {
        epoch++; images.Clear(); pending = null; selectedRoute = null; granted = null;
        expiry.Stop(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; conversation.PropertyChanged -= OnChanged;
        conversation.SessionChanged -= Clear; conversation.RequestCancelled -= OnRequestCancelled;
        conversation.DesktopMessagePersisted -= Bind;
        expiry.Tick -= OnExpiry; Clear();
    }
}
