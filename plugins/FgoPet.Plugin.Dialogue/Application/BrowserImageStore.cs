using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>One run-scoped browser capture; separate from the user's pending picker attachment.</summary>
public sealed class BrowserImageStore(SelectedImageStore selected, Func<ToolScope, bool> active,
    Func<ModelRouteKey?> route, TimeProvider clock) : IBrowserImageSink, IChatImageResolver, IDisposable
{
    private readonly object gate = new();
    private sealed record Entry(ToolScope Scope, string RunId, ModelRouteKey Route, ToolImageReference Reference,
        ChatImageInput Image, Func<bool> Authorized, DateTimeOffset Expires);
    private Entry? current;
    private string? consumedRun;
    private bool disposed;
    public string? Check(ToolScope scope, string runId)
    {
        lock (gate)
        {
            if (disposed) return "IMAGE_SELECTION_UNAVAILABLE";
            if (current is not null && clock.GetUtcNow() >= current.Expires) current = null;
            if (string.IsNullOrWhiteSpace(runId) || !active(scope)) return "IMAGE_SCOPE_DENIED";
            if (route() is not { } model || !ImageModelCapabilities.Supports(model)) return "IMAGE_MODEL_UNSUPPORTED";
            if (selected.HasSelection) return "IMAGE_SELECTION_CONFLICT";
            return consumedRun is null ? null : "IMAGE_RUN_LIMIT";
        }
    }
    public BrowserImageAttachment Accept(ToolScope scope, string runId, byte[] bytes, string mimeType,
        Func<bool> authorizationCurrent, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (Check(scope, runId) is { } error) throw new BrowserImageException(error);
            if (!authorizationCurrent()) throw new BrowserImageException("BROWSER_AUTHORIZATION_CHANGED");
            var originalRoute = route()!;
            ChatImageInput image;
            try { image = SelectedImageDecoder.Decode(bytes, mimeType switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED") }, token, isBrowserImage: true); }
            catch (SelectedImageException decodeError) { throw new BrowserImageException(decodeError.Code); }
            if (Check(scope, runId) is { } changed) throw new BrowserImageException(changed);
            if (!authorizationCurrent() || originalRoute != route()) throw new BrowserImageException("BROWSER_AUTHORIZATION_CHANGED");
            var reference = new ToolImageReference(Guid.NewGuid().ToString("N"));
            current = new(scope, runId, originalRoute, reference, image, authorizationCurrent, clock.GetUtcNow() + TimeSpan.FromMinutes(5));
            consumedRun = runId;
            return new(reference, image.Width, image.Height, image.ByteLength);
        }
    }
    internal bool Owns(ToolImageReference reference) { lock (gate) return current?.Reference == reference; }
    public ChatImageInput Resolve(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey model, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var entry = current;
            if (entry is null || entry.Reference != reference) throw new SelectedImageException("IMAGE_SELECTION_UNAVAILABLE");
            if (entry.Expires <= clock.GetUtcNow() || !entry.Authorized() || !active(entry.Scope)
                || entry.Route != model || route() != model || selected.HasSelection)
            { current = null; throw new SelectedImageException("IMAGE_SELECTION_CHANGED"); }
            if (entry.Scope.RoleId != roleId || entry.Scope.ConversationId != conversationId)
                throw new SelectedImageException("IMAGE_SCOPE_DENIED");
            return entry.Image;
        }
    }
    public IDisposable AcquireDispatch(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey model, CancellationToken token)
    {
        Monitor.Enter(gate);
        try { _ = Resolve(reference, roleId, conversationId, model, token); return new Lease(gate); }
        catch { Monitor.Exit(gate); throw; }
    }
    private sealed class Lease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    public void Release(string runId) { lock (gate) { if (consumedRun == runId) { current = null; consumedRun = null; } } }
    public void Dispose() { lock (gate) { disposed = true; current = null; consumedRun = null; } }
}

internal sealed class ConversationImageResolver(SelectedImageStore selected, BrowserImageStore browser) : IChatImageResolver
{
    private IChatImageResolver Owner(ToolImageReference reference) => browser.Owns(reference) ? browser : selected;
    public ChatImageInput Resolve(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey route, CancellationToken token) =>
        Owner(reference).Resolve(reference, roleId, conversationId, route, token);
    public IDisposable AcquireDispatch(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey route, CancellationToken token) =>
        Owner(reference).AcquireDispatch(reference, roleId, conversationId, route, token);
}
