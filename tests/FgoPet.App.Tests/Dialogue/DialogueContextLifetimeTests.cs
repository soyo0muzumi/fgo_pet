using FgoPet.App.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class DialogueContextLifetimeTests
{
    [Fact]
    public async Task Maintenance_prevents_new_leases_until_deletion_finishes()
    {
        var lifetime = new DialogueContextLifetime();
        using (await lifetime.SuspendAsync(default))
            Assert.Throws<OperationCanceledException>(() => lifetime.Acquire(default));
        using var allowed = lifetime.Acquire(default);
        allowed.CheckCurrent();
    }
    [Fact]
    public async Task Invalidation_cancels_old_work_waits_for_drain_and_rejects_stale_commits()
    {
        var lifetime = new DialogueContextLifetime();
        var old = lifetime.Acquire(default);
        var oldGeneration = lifetime.Generation;
        var draining = lifetime.InvalidateAsync(default);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(draining.IsCompleted);
        Assert.True(lifetime.Generation > oldGeneration);
        Assert.False(old.TryCommit(() => throw new Exception("stale commit")));
        old.Dispose();
        await draining.WaitAsync(TimeSpan.FromSeconds(5));
        using var current = lifetime.Acquire(default);
        var saved = false;
        Assert.True(current.TryCommit(() => saved = true));
        Assert.True(saved);
    }
}
