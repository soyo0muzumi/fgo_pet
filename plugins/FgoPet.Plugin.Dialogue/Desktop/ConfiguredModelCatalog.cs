using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Dialogue;

/// <summary>Models for the persisted connection only. Never consumes settings draft results or secrets.</summary>
public sealed class ConfiguredModelCatalog : IDisposable
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly Func<ModelConnectionSettings?> readConnection;
    private readonly Func<ModelConnectionSettings, CancellationToken, Task<IReadOnlyList<ProviderModel>>> fetch;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private (string Provider, string Endpoint)? route;
    private IReadOnlyList<DialogueModelChoice> models = [];
    private DateTimeOffset retryAfter;
    private CancellationTokenSource? pending;
    private Task? flight;
    private long generation;
    private bool disposed;
    private string status = "";

    public ConfiguredModelCatalog(Func<ModelConnectionSettings?> readConnection,
        Func<ModelConnectionSettings, CancellationToken, Task<IReadOnlyList<ProviderModel>>> fetch,
        TimeProvider? clock = null)
    { this.readConnection = readConnection; this.fetch = fetch; this.clock = clock ?? TimeProvider.System; }

    public event EventHandler? Changed;
    public IReadOnlyList<DialogueModelChoice> Models { get { lock (gate) return route == Key(readConnection()) ? models : []; } }
    public bool IsRefreshing { get { lock (gate) return flight is { IsCompleted: false }; } }
    public string Status { get { lock (gate) return status; } }
    public string? SelectedModelId => readConnection()?.ModelId;
    private static (string, string)? Key(ModelConnectionSettings? connection) => connection is null
        ? null : (connection.ProviderId, connection.BaseUrl.TrimEnd('/'));

    public void Invalidate()
    {
        CancellationTokenSource? old;
        lock (gate)
        {
            generation++; old = pending; pending = null; flight = null;
            route = null; models = []; retryAfter = default; status = "";
        }
        CancelPendingRefresh(old);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void CancelPendingRefresh(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // FetchAsync may finish and dispose the detached source before this
            // caller cancels it. That completed refresh has no work left to cancel.
        }
    }

    public Task RefreshAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task task;
        ModelConnectionSettings? connection;
        TaskCompletionSource? completion = null;
        CancellationTokenSource? cancellation = null;
        long version = 0;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            connection = readConnection();
            if (route != Key(connection)) Invalidate();
            if (connection is null) return Task.CompletedTask;
            if (flight is { IsCompleted: false }) task = flight;
            else if (clock.GetUtcNow() < retryAfter) return Task.CompletedTask;
            else
            {
                route = Key(connection); version = generation;
                pending = cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                flight = task = completion.Task; status = "正在更新模型列表…";
            }
        }
        if (completion is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            _ = FetchAsync(connection!, version, cancellation!, completion);
        }
        // A caller leaving the view cancels only its wait; other callers share the same refresh.
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }

    private async Task FetchAsync(ModelConnectionSettings connection, long version,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try
        {
            var result = await fetch(connection, cancellation.Token).WaitAsync(cancellation.Token);
            lock (gate)
            {
                if (disposed || generation != version || route != Key(readConnection())) return;
                models = result.Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => new DialogueModelChoice(m.Id.Trim(), m.DisplayName))
                    .DistinctBy(m => m.Id, StringComparer.Ordinal).ToArray();
                retryAfter = clock.GetUtcNow() + CacheLifetime; status = "模型列表已更新。";
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException
            or System.Net.Http.HttpRequestException or ProviderRequestException or System.IO.IOException)
        {
            lock (gate)
            {
                if (disposed || generation != version || route != Key(readConnection())) return;
                retryAfter = clock.GetUtcNow() + TimeSpan.FromSeconds(30);
                status = models.Count == 0 ? "模型列表更新失败，请稍后重试。" : "更新失败，仍显示上次成功的模型列表。";
            }
        }
        finally
        {
            lock (gate) { if (ReferenceEquals(pending, cancellation)) { pending = null; flight = null; } }
            cancellation.Dispose(); completion.TrySetResult();
            if (!disposed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; } Invalidate(); }
}
