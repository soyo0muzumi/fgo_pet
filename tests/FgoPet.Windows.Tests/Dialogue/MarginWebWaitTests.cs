using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class MarginWebWaitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transient_second_sample_is_retried_until_a_stable_pair_within_the_original_deadline(bool initialFalse)
    {
        var pending = new Queue<bool>(initialFalse ? [false, true, false, true, true] : [true, false, true, true]);
        var observed = new List<bool>();
        await MarginWebIntegrationTests.WaitUntil(() =>
        {
            Assert.NotEmpty(pending);
            var value = pending.Dequeue(); observed.Add(value);
            return Task.FromResult(value);
        });
        Assert.Empty(pending);
        Assert.Equal([true, true], observed.TakeLast(2));
        Assert.Contains(false, observed);
    }
}
