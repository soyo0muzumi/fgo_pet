using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

public sealed class SelectedImageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Validated, metadata-free PNG. Kept in memory only, never in a checkpoint or semantic message.</summary>
public sealed class ChatImageInput
{
    private readonly byte[] bytes;
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public const int MaxPixels = 4 * 1024 * 1024;
    public ChatImageInput(byte[] pngBytes, int width, int height, bool isBrowserImage = false)
    {
        if (pngBytes is null || pngBytes.Length is < 8 or > MaxBytes
            || !pngBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || width is < 1 or > MaxDimension || height is < 1 or > MaxDimension
            || (long)width * height > MaxPixels) throw new SelectedImageException("IMAGE_INVALID_DATA");
        bytes = pngBytes.ToArray(); Width = width; Height = height; IsBrowserImage = isBrowserImage;
        Fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
    }
    public int Width { get; }
    public bool IsBrowserImage { get; }
    public int Height { get; }
    public int ByteLength => bytes.Length;
    public string Fingerprint { get; }
    public string ToDataUrl() => "data:image/png;base64," + Convert.ToBase64String(bytes);
}

public interface IChatImageResolver
{
    ChatImageInput Resolve(ToolImageReference reference, string roleId, string conversationId,
        ModelRouteKey route, CancellationToken token);
    /// <summary>Short synchronous lease, sharing the revocation lock. Never retain across an await.</summary>
    IDisposable AcquireDispatch(ToolImageReference reference, string roleId, string conversationId,
        ModelRouteKey route, CancellationToken token);
}

/// <summary>Provider opt-in. Preparation is performed before input budgeting, validation again at HTTP dispatch.</summary>
public interface IChatImageInputProvider
{
    void PrepareImages(ChatRequest request, CancellationToken token);
}

public static class ChatImageInputs
{
    private sealed record Prepared(IReadOnlyDictionary<string, ChatImageInput> Images);
    private static readonly ConditionalWeakTable<ChatRequest, Prepared> State = new();
    public static bool HasImages(ChatRequest request) => request.IsAgentRequest && request.ModelMessages.Any(m => m.Image is not null);
    public static void Prepare(ChatRequest request, ModelRouteKey route, IChatImageResolver? resolver, bool supported, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasImages(request)) return;
        if (!supported) throw new SelectedImageException("IMAGE_MODEL_UNSUPPORTED");
        if (resolver is null) throw new SelectedImageException("IMAGE_SELECTION_UNAVAILABLE");
        var references = request.ModelMessages.Where(m => m.Image is not null).Select(m => m.Image!).Distinct().ToArray();
        if (references.Length != 1) throw new SelectedImageException("IMAGE_COUNT_LIMIT");
        var images = references.ToDictionary(r => r.Id, r => resolver.Resolve(r, request.ServantId, request.ConversationId, route, token));
        State.Remove(request);
        State.Add(request, new(images));
    }
    public static ChatImageInput Read(ChatRequest request, ToolImageReference reference) =>
        State.TryGetValue(request, out var prepared) && prepared.Images.TryGetValue(reference.Id, out var image)
            ? image : throw new SelectedImageException("IMAGE_NOT_PREPARED");
    public static T Dispatch<T>(ChatRequest request, ModelRouteKey route, IChatImageResolver? resolver,
        Func<T> start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasImages(request)) return start();
        if (resolver is null) throw new SelectedImageException("IMAGE_SELECTION_UNAVAILABLE");
        var references = request.ModelMessages.Where(m => m.Image is not null).Select(m => m.Image!).Distinct().ToArray();
        if (references.Length != 1) throw new SelectedImageException("IMAGE_COUNT_LIMIT");
        using var lease = resolver.AcquireDispatch(references[0], request.ServantId, request.ConversationId, route, token);
        Validate(request, route, resolver, token);
        return start();
    }
    public static void Validate(ChatRequest request, ModelRouteKey route, IChatImageResolver? resolver, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasImages(request)) return;
        if (resolver is null) throw new SelectedImageException("IMAGE_SELECTION_UNAVAILABLE");
        foreach (var reference in request.ModelMessages.Where(m => m.Image is not null).Select(m => m.Image!).Distinct())
        {
            var current = resolver.Resolve(reference, request.ServantId, request.ConversationId, route, token);
            if (!ReferenceEquals(current, Read(request, reference))) throw new SelectedImageException("IMAGE_SELECTION_CHANGED");
        }
    }
}

/// <summary>One explicitly selected snapshot. No local path is retained, and every access rechecks live scope and route.</summary>
public sealed class SelectedImageStore(Func<ToolScope, bool> isActive, TimeProvider? time = null,
    Func<ModelRouteKey?>? currentRoute = null) : IChatImageResolver
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private sealed record Selection(ToolScope Scope, ModelRouteKey Route, ChatImageInput Image, ToolImageReference Reference,
        DateTimeOffset ExpiresAt, string UserMessageId, string? RunId = null);
    private Selection? selected;
    public bool HasSelection { get { lock (gate) return selected is not null && clock.GetUtcNow() < selected.ExpiresAt; } }
    public void Grant(ToolScope scope, ModelRouteKey route, ChatImageInput image, string userMessageId, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(scope.ConversationId) || !isActive(scope)) throw new SelectedImageException("IMAGE_SCOPE_DENIED");
        if (string.IsNullOrWhiteSpace(userMessageId)) throw new SelectedImageException("IMAGE_RUN_DENIED");
        var deadline = expiresAt ?? clock.GetUtcNow() + Lifetime;
        if (deadline <= clock.GetUtcNow() || deadline > clock.GetUtcNow() + Lifetime) throw new SelectedImageException("IMAGE_SELECTION_EXPIRED");
        lock (gate) selected = new(scope, route, image, new(Guid.NewGuid().ToString("N")), deadline, userMessageId);
    }
    /// <summary>Only the native run rooted at the trusted desktop send may acquire this selection.</summary>
    public void Bind(ToolScope scope, ModelRouteKey route, string userMessageId, string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var selection = Check(scope.RoleId, scope.ConversationId, route);
            if (selection.Scope != scope) throw new SelectedImageException("IMAGE_SCOPE_DENIED");
            if (string.IsNullOrWhiteSpace(runId) || selection.UserMessageId != userMessageId
                || selection.RunId is not null && selection.RunId != runId)
                throw new SelectedImageException("IMAGE_RUN_DENIED");
            selected = selection with { RunId = runId };
        }
    }
    public ToolImageReference Read(ToolScope scope, ModelRouteKey route, string? runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var selection = Check(scope.RoleId, scope.ConversationId, route);
            if (selection.Scope != scope) throw new SelectedImageException("IMAGE_SCOPE_DENIED");
            if (string.IsNullOrWhiteSpace(runId) || selection.RunId != runId) throw new SelectedImageException("IMAGE_RUN_DENIED");
            return selection.Reference;
        }
    }
    public ChatImageInput Resolve(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey route, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var selection = Check(roleId, conversationId, route);
            if (selection.RunId is null) throw new SelectedImageException("IMAGE_RUN_DENIED");
            if (selection.Reference != reference) throw new SelectedImageException("IMAGE_SELECTION_UNAVAILABLE");
            return selection.Image;
        }
    }
    public IDisposable AcquireDispatch(ToolImageReference reference, string roleId, string conversationId,
        ModelRouteKey route, CancellationToken token)
    {
        Monitor.Enter(gate);
        try
        {
            token.ThrowIfCancellationRequested();
            _ = Resolve(reference, roleId, conversationId, route, token);
            return new DispatchLease(gate);
        }
        catch { Monitor.Exit(gate); throw; }
    }
    private sealed class DispatchLease(object gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(gate); } }
    }
    private Selection Check(string roleId, string conversationId, ModelRouteKey route)
    {
        var selection = selected ?? throw new SelectedImageException("IMAGE_SELECTION_REQUIRED");
        if (clock.GetUtcNow() >= selection.ExpiresAt)
        { selected = null; throw new SelectedImageException("IMAGE_SELECTION_EXPIRED"); }
        if (selection.Scope.RoleId != roleId || selection.Scope.ConversationId != conversationId || !isActive(selection.Scope))
            throw new SelectedImageException("IMAGE_SCOPE_DENIED");
        if (selection.Route != route || (currentRoute is not null && currentRoute() != route))
            throw new SelectedImageException("IMAGE_MODEL_CHANGED");
        return selection;
    }
    public void Clear() { lock (gate) selected = null; }
}
