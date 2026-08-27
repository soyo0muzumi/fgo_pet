using FgoPet.App.Panels;
using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.App.Tests.Panels;

public sealed class AttachedPanelViewModelTests
{
    private const string Epoch = "2026-08-27T09:00:00Z";

    [Fact]
    public void Startup_is_always_collapsed()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        Assert.Equal(AttachedPanelState.Collapsed, vm.State);
    }

    [Fact]
    public void Portrait_click_steps_into_compact_and_toggling_back_out()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));

        vm.PortraitClick();
        Assert.Equal(AttachedPanelState.Compact, vm.State);

        vm.PortraitClick();
        Assert.Equal(AttachedPanelState.Collapsed, vm.State);
    }

    [Fact]
    public void Dialogue_click_expands_steps_down_and_back()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        vm.PortraitClick();
        Assert.Equal(AttachedPanelState.Compact, vm.State);

        vm.DialogueClick();
        Assert.Equal(AttachedPanelState.ExpandedDialogue, vm.State);

        vm.DialogueClick();
        Assert.Equal(AttachedPanelState.Compact, vm.State);
    }

    [Fact]
    public void Todo_click_expands_and_escape_steps_down_then_collapses()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        vm.PortraitClick();
        vm.TodoClick();
        Assert.Equal(AttachedPanelState.ExpandedTodo, vm.State);

        vm.Escape();
        Assert.Equal(AttachedPanelState.Compact, vm.State);

        vm.Escape();
        Assert.Equal(AttachedPanelState.Collapsed, vm.State);
    }

    [Fact]
    public void Idle_collapses_an_expanded_panel_back_to_compact()
    {
        var time = new MutableTimeProvider(Epoch);
        var vm = new AttachedPanelViewModel(time);
        vm.PortraitClick();
        vm.DialogueClick();
        Assert.Equal(AttachedPanelState.ExpandedDialogue, vm.State);

        vm.PointerLeft();
        time.Now = time.Now.AddMinutes(1);
        vm.Tick();

        Assert.Equal(AttachedPanelState.Compact, vm.State);
    }

    [Fact]
    public void Idle_is_suppressed_while_the_pointer_is_inside()
    {
        var time = new MutableTimeProvider(Epoch);
        var vm = new AttachedPanelViewModel(time);
        vm.PortraitClick();
        vm.DialogueClick();

        vm.PointerEntered();
        time.Now = time.Now.AddMinutes(1);
        vm.Tick();

        Assert.Equal(AttachedPanelState.ExpandedDialogue, vm.State);
    }

    [Fact]
    public void Dialogue_is_bounded_to_twenty_and_presents_six()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        foreach (var text in PanelFixtures.LongChineseDialogue(21))
        {
            vm.AddDialogue(text);
        }

        Assert.Equal(20, vm.Dialogue.Count);
        Assert.Equal(PanelFixtures.LongChinese(), vm.Dialogue[0].Text);
        Assert.Equal(6, vm.VisibleDialogueCount);
    }

    [Fact]
    public void Long_chinese_and_unbroken_english_dialogue_are_accepted()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        vm.AddDialogue(PanelFixtures.LongChinese());
        vm.AddDialogue(PanelFixtures.UnbrokenEnglish());

        Assert.Equal(2, vm.Dialogue.Count);
    }

    [Fact]
    public void Todo_overflows_after_eight_rows_and_still_scrolls()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        for (var index = 1; index <= 10; index++)
        {
            vm.AddTodo($"待办 {index}");
        }

        Assert.True(vm.TodoOverflows);
        Assert.Equal(8, vm.VisibleTodoCount);
        Assert.Equal(10, vm.Todo.Count);
    }

    [Fact]
    public void Empty_lists_report_zero_visible_items()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch));
        Assert.Equal(0, vm.VisibleDialogueCount);
        Assert.Equal(0, vm.VisibleTodoCount);
        Assert.False(vm.TodoOverflows);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(string utcNow) => Now = DateTimeOffset.Parse(utcNow);

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}