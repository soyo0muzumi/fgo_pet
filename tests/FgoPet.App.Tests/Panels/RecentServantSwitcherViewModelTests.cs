using FgoPet.App.Panels;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.App.Tests.Panels;

public sealed class RecentServantSwitcherViewModelTests
{
    private readonly FakePortraitController _controller = new();
    private readonly RecentServantSwitcherViewModel _vm;

    public RecentServantSwitcherViewModelTests() => _vm = new RecentServantSwitcherViewModel(_controller);

    [Fact]
    public void The_switcher_appears_once_three_recent_servants_exist()
    {
        _vm.RecordUsed(Selection("a"));
        _vm.RecordUsed(Selection("b"));
        Assert.False(_vm.HasEnough);

        _vm.RecordUsed(Selection("c"));
        Assert.True(_vm.HasEnough);
        Assert.Equal(3, _vm.Entries.Count);
    }

    [Fact]
    public void The_switcher_caps_at_five_and_evicts_the_oldest()
    {
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" })
        {
            _vm.RecordUsed(Selection(id));
        }

        Assert.Equal(5, _vm.Entries.Count);
        Assert.DoesNotContain(Selection("a"), _vm.Entries);
        Assert.Equal(Selection("f"), _vm.Entries[^1]);
    }

    [Fact]
    public void Reusing_a_servant_moves_it_to_the_most_recent_slot()
    {
        _vm.RecordUsed(Selection("a"));
        _vm.RecordUsed(Selection("b"));
        _vm.RecordUsed(Selection("a"));

        Assert.Equal(2, _vm.Entries.Count);
        Assert.Equal(Selection("a"), _vm.Entries[^1]);
    }

    [Fact]
    public async Task Activating_delegates_to_the_portrait_controller()
    {
        await _vm.ActivateAsync(Selection("a"), CancellationToken.None);
        Assert.Equal(Selection("a"), Assert.Single(_controller.Activations));
    }

    private static PortraitSelection Selection(string id) => new($"pkg.{id}", "casual", "1.0.0");

    private sealed class FakePortraitController : IPortraitController
    {
        public List<PortraitSelection> Activations { get; } = new();

        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            Activations.Add(selection);
            return Task.CompletedTask;
        }

        public void SetExpression(ExpressionSemantic semantic)
        {
        }

        public void SetScale(double scale)
        {
        }

        public void ApplyDpi(Dpi2 dpi)
        {
        }
    }
}