using FgoPet.AgentRuntime;
using Xunit;

namespace FgoPet.AgentRuntime.Tests;

public sealed class RelaySingleInstanceTests
{
    [Fact]
    public async Task Only_one_owner_can_acquire_the_same_mutex_from_independent_threads()
    {
        var name = "Local\\FgoPet.Test." + Guid.NewGuid().ToString("N");
        using var first = RelaySingleInstance.TryAcquire(name);
        RelaySingleInstance? second = null;
        try
        {
            await Task.Run(() => second = RelaySingleInstance.TryAcquire(name));

            Assert.True(first.IsOwner);
            Assert.NotNull(second);
            Assert.False(second.IsOwner);
        }
        finally
        {
            second?.Dispose();
        }
    }

    [Fact]
    public async Task Owner_can_be_disposed_from_a_different_thread_without_losing_mutex_lifetime()
    {
        var name = "Local\\FgoPet.Test." + Guid.NewGuid().ToString("N");
        using var first = RelaySingleInstance.TryAcquire(name);
        Assert.True(first.IsOwner);

        await Task.Run(first.Dispose);

        using var second = RelaySingleInstance.TryAcquire(name);
        Assert.True(second.IsOwner);
    }

    [Fact]
    public void Pipe_suffix_is_validated_and_mutex_name_is_stable_for_the_current_user()
    {
        var options = TestOptions("unit-test");
        var names = RelayPipeNames.ForCurrentUser(options);

        Assert.Contains("fgo-pet-agent-adapter-", names.Adapter);
        Assert.EndsWith("-unit-test", names.Adapter, StringComparison.Ordinal);
        Assert.Contains("fgo-pet-agent-app-", names.App);
        Assert.NotEqual(names.Adapter, names.App);
        Assert.StartsWith("Local\\FgoPet.AgentRelay.", names.Mutex, StringComparison.Ordinal);
        Assert.Equal(names.Mutex, RelayPipeNames.ForCurrentUser(options).Mutex);
    }

    [Theory]
    [InlineData("bad\\suffix")]
    [InlineData("bad/suffix")]
    [InlineData("bad suffix")]
    public void Invalid_pipe_suffix_is_rejected(string suffix)
    {
        Assert.Throws<ArgumentException>(() => TestOptions(suffix));
    }

    private static RelayRuntimeOptions TestOptions(string suffix = "test") => new(
        suffix,
        Path.Combine(Path.GetTempPath(), "FgoPet-AgentRuntime-Tests", Guid.NewGuid().ToString("N")),
        Path.Combine(AppContext.BaseDirectory, "FgoPet.AgentRelay.exe"),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500));
}
