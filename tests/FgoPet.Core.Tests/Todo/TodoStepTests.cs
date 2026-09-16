using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Core.Tests.Todo;

public sealed class TodoStepTests
{
    [Fact]
    public void Step_normalizes_text_and_keeps_completion_state()
    {
        var step = new TodoStep(" step-1 ", " Prepare ", 0, isCompleted: true);

        Assert.Equal("step-1", step.Id);
        Assert.Equal("Prepare", step.Title);
        Assert.Equal(0, step.Order);
        Assert.True(step.IsCompleted);
    }

    [Fact]
    public void Step_rejects_empty_or_overlong_title()
    {
        Assert.Throws<ArgumentException>(() => new TodoStep("step-1", "  ", 0));
        Assert.Throws<ArgumentException>(() => new TodoStep("step-1", new string('x', 201), 0));
    }

    [Fact]
    public void Step_rejects_invalid_id_or_order()
    {
        Assert.Throws<ArgumentException>(() => new TodoStep("  ", "Prepare", 0));
        Assert.Throws<ArgumentException>(() => new TodoStep(new string('x', 129), "Prepare", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TodoStep("step-1", "Prepare", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TodoStep("step-1", "Prepare", 20));
    }

    [Fact]
    public void Step_is_immutable_after_creation()
    {
        var step = new TodoStep("step-1", "Prepare", 0);

        Assert.Equal(new TodoStep("step-1", "Prepare", 0), step);
        Assert.Equal("Prepare", step.Title);
    }
}
