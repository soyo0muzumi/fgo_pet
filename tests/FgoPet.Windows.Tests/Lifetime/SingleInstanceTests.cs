using System.IO;
using System.Threading.Tasks;
using FgoPet.App.Lifetime;
using Xunit;

namespace FgoPet.Windows.Tests.Lifetime;

[Trait("Category", "WindowsIntegration")]
public sealed class SingleInstanceTests
{
    [Fact]
    public void Only_the_first_instance_becomes_primary()
    {
        var appId = $"win-single-{Guid.NewGuid():N}";
        var acquired = SingleInstanceCoordinator.TryCreatePrimary(appId, out var coordinator, out var primaryHeld);
        Assert.True(acquired);
        Assert.NotNull(coordinator);
        Assert.True(primaryHeld);
        using (coordinator!)
        {
            var second = SingleInstanceCoordinator.TryCreatePrimary(appId, out var secondCoordinator, out var secondHeld);
            Assert.False(second);
            Assert.False(secondHeld);
        }
    }

    [Fact]
    public async Task A_secondary_forwards_its_path_to_the_primary()
    {
        var appId = $"win-single-fwd-{Guid.NewGuid():N}";
        var acquired = SingleInstanceCoordinator.TryCreatePrimary(appId, out var coordinator, out _);
        Assert.True(acquired);
        using (coordinator!)
        {
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.ListenForActivation(path => received.TrySetResult(path));

            var forwarded = SingleInstanceCoordinator.ForwardActivation(appId, "C:\\tmp\\mash.fgopetpack", TimeSpan.FromSeconds(10));

            Assert.True(forwarded);
            var path = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("C:\\tmp\\mash.fgopetpack", path);
        }
    }

    [Fact]
    public void ForwardActivation_returns_false_when_no_primary_is_listening()
    {
        var appId = $"win-single-none-{Guid.NewGuid():N}";
        var forwarded = SingleInstanceCoordinator.ForwardActivation(appId, "C:\\tmp\\x.fgopetpack", TimeSpan.FromSeconds(2));
        Assert.False(forwarded);
    }
}