using System.Collections.Specialized;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Runtime;
using FgoPet.Core.Bond;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Panels;
using FgoPet.Core.Timeline;
using Xunit;

namespace FgoPet.App.Tests.Panels;

public sealed class Phase2AttachedPanelViewModelTests
{
    private const string Epoch = "2026-08-27T09:00:00Z";

    private readonly FakeFocusService _focus;
    private readonly AttachedPanelViewModel _vm;

    public Phase2AttachedPanelViewModelTests()
    {
        _focus = new FakeFocusService();
        _vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), _focus);
    }

    [Fact]
    public void Running_focus_replaces_compact_message_with_timer_without_expanding()
    {
        _vm.PortraitClick();
        _focus.Current = FocusingWithRemaining(1_458);
        _focus.RaiseChanged();

        Assert.Equal(AttachedPanelState.Compact, _vm.State);
        Assert.True(_vm.IsCompactTimerVisible);
        Assert.Equal("24:18", _vm.RemainingText);
        Assert.Equal("第 1 / 4 轮", _vm.CycleText);
        Assert.Equal("本轮 25:00 · 已完成 3%", _vm.TimerMetaText);
        Assert.Equal(2.8, _vm.ProgressPercent, 1);
    }

    [Fact]
    public void Active_role_state_enables_focus_without_a_role_library_event()
    {
        var runtime = new AppRuntime();
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), _focus, runtime: runtime);

        runtime.SetActiveRole(new ActiveRoleState("pack", "casual", "1.0.0", "servant-mash"));

        Assert.True(vm.CanStartFocus);
        Assert.Equal("servant-mash", vm.ActiveServantId);
    }

    [Fact]
    public void Custom_summary_excludes_the_break_after_the_last_cycle()
    {
        _vm.SelectCustomPreset();
        _vm.CustomFocusMinutesText = "35";
        _vm.CustomBreakMinutesText = "10";
        _vm.CustomCyclesText = "3";

        Assert.Equal("02:05:00", _vm.CustomTotalText);
    }

    [Fact]
    public void Custom_step_controls_use_approved_steps_and_clamp_to_bounds()
    {
        _vm.SelectCustomPreset();
        _vm.CustomFocusMinutesText = "178";
        _vm.CustomBreakMinutesText = "1";
        _vm.CustomCyclesText = "12";

        _vm.AdjustCustomFocus(1);
        _vm.AdjustCustomBreak(-1);
        _vm.AdjustCustomCycles(1);

        Assert.Equal("180", _vm.CustomFocusMinutesText);
        Assert.Equal("1", _vm.CustomBreakMinutesText);
        Assert.Equal("12", _vm.CustomCyclesText);
    }

    [Fact]
    public void Paused_timer_shows_the_paused_phase_label()
    {
        _vm.PortraitClick();
        _focus.Current = FocusingWithRemaining(1_458).RestorePaused();
        _focus.RaiseChanged();

        Assert.True(_vm.IsCompactTimerVisible);
        Assert.Equal("24:18", _vm.RemainingText);
        Assert.True(_vm.IsPaused);
    }

    [Fact]
    public void Break_timer_shows_the_break_label()
    {
        _vm.PortraitClick();
        _focus.Current = FocusingWithRemaining(1_458) with
        {
            Status = FocusStatus.Breaking,
            Phase = FocusPhase.Break,
        };
        _focus.RaiseChanged();

        Assert.True(_vm.IsCompactTimerVisible);
        Assert.Contains("休息", _vm.PhaseText);
    }

    [Fact]
    public void Idle_session_shows_the_character_message_not_the_timer()
    {
        _vm.PortraitClick();
        _focus.Current = FocusSession.Idle;
        _focus.RaiseChanged();

        Assert.False(_vm.IsCompactTimerVisible);
    }

    [Fact]
    public void FocusClick_expands_focus_and_updates_state()
    {
        _vm.PortraitClick();
        _vm.FocusClick();

        Assert.Equal(AttachedPanelState.ExpandedFocus, _vm.State);
    }

    [Fact]
    public void TodayClick_expands_today_and_updates_state()
    {
        _vm.PortraitClick();
        _vm.TodayClick();

        Assert.Equal(AttachedPanelState.ExpandedToday, _vm.State);
    }

    [Fact]
    public void FocusClick_from_expanded_dialogue_switches_to_focus()
    {
        _vm.PortraitClick();
        _vm.DialogueClick();
        _vm.FocusClick();

        Assert.Equal(AttachedPanelState.ExpandedFocus, _vm.State);
    }

    [Fact]
    public void Invalid_custom_minutes_disable_start_and_suppress_idle_collapse()
    {
        _vm.PortraitClick();
        _vm.FocusClick();
        _vm.SelectCustomPreset();
        _vm.CustomFocusMinutesText = "4";

        Assert.False(_vm.CanStartFocus);
        Assert.True(_vm.IsEditingCustomPreset);
        Assert.NotEmpty(_vm.CustomFocusError);
    }

    [Fact]
    public void Valid_custom_minutes_enable_start_and_clear_the_error()
    {
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _vm.SelectCustomPreset();
        _vm.CustomFocusMinutesText = "45";
        _vm.CustomBreakMinutesText = "9";
        _vm.CustomCyclesText = "3";

        Assert.True(_vm.CanStartFocus);
        Assert.Empty(_vm.CustomFocusError);
        Assert.Empty(_vm.CustomBreakError);
        Assert.Empty(_vm.CustomCyclesError);
    }

    [Fact]
    public void Start_focus_uses_the_selected_builtin_preset_and_current_servant()
    {
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _vm.SelectPreset(FocusPresetCatalog.Short);
        _vm.StartFocus();

        Assert.Equal(FocusPresetCatalog.Short, _focus.StartedPreset);
        Assert.Equal("servant-mash", _focus.StartedServantId);
    }

    [Fact]
    public void Timer_commands_forward_to_the_focus_service()
    {
        _focus.Current = FocusingWithRemaining(1_458).RestorePaused();
        _focus.RaiseChanged();

        _vm.PauseTimer();
        _vm.ResumeTimer();
        _vm.StopTimer();

        Assert.Equal(1, _focus.Pauses);
        Assert.Equal(1, _focus.Resumes);
        Assert.Equal(1, _focus.Stops);
    }

    [Fact]
    public void Today_items_refresh_from_the_query_and_show_bond_text()
    {
        _vm.SetActiveServant("servant-mash");
        _focus.Current = FocusingWithRemaining(1_458);
        _focus.RaiseChanged();
        _vm.RefreshToday(new[]
        {
            new TimelineEntry("entry-1", "event-1", DateTimeOffset.Parse("2026-08-27T09:25:00Z"),
                RuntimeEventType.FocusCompleted, "servant-mash", 1_500, 1_500, null),
            new TimelineEntry("entry-2", "event-2", DateTimeOffset.Parse("2026-08-27T10:00:00Z"),
                RuntimeEventType.BondLevelUp, "servant-mash", 0, 0, 3),
        });
        _vm.RefreshBond(new BondProgress(3, 11_700, 10_800, 21_600, false));

        Assert.Equal(2, _vm.Today.Count);
        Assert.Equal("3", _vm.BondLevelText);
        Assert.Contains("小时", _vm.BondRemainingText);
        Assert.Contains("25", _vm.TodayEffectiveText);
    }

    [Fact]
    public void Servant_change_during_editing_keeps_the_panel_open_but_updates_the_owner()
    {
        _vm.PortraitClick();
        _vm.FocusClick();
        _vm.SelectCustomPreset();
        _vm.CustomFocusMinutesText = "4";

        _vm.SetActiveServant("servant-other");

        Assert.Equal(AttachedPanelState.ExpandedFocus, _vm.State);
        Assert.Equal("servant-other", _vm.ActiveServantId);
    }

    [Fact]
    public void Starting_from_the_focus_column_steps_down_to_compact_and_shows_the_timer()
    {
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _vm.SelectPreset(FocusPresetCatalog.Short);
        _vm.StartFocus();
        _focus.RaiseChanged();

        Assert.Equal(AttachedPanelState.Compact, _vm.State);
        Assert.True(_vm.IsCompactTimerVisible);
    }

    [Fact]
    public void Servant_resolution_is_required_before_start_is_enabled()
    {
        _vm.PortraitClick();
        _vm.FocusClick();

        Assert.False(_vm.CanStartFocus);

        _vm.SetActiveServant("servant-mash");
        Assert.True(_vm.CanStartFocus);
    }

    private static FocusSession FocusingWithRemaining(int remaining) => FocusSession.Start(
        "session-1", "servant-mash", FocusPreset.Create(25, 5, 4),
        DateTimeOffset.Parse(Epoch)) with { RemainingSeconds = remaining };

    private sealed class FakeFocusService : IFocusSessionService
    {
        public FocusSession Current { get; set; } = FocusSession.Idle;

        public event EventHandler? SnapshotChanged;

        public event EventHandler? PersistenceFailed
        {
            add { }
            remove { }
        }

        public FocusPreset? StartedPreset { get; private set; }

        public string? StartedServantId { get; private set; }

        public int Pauses { get; private set; }

        public int Resumes { get; private set; }

        public int Stops { get; private set; }

        public void Start(FocusPreset preset, string servantId)
        {
            StartedPreset = preset;
            StartedServantId = servantId;
            Current = FocusSession.Start("new-session", servantId, preset, DateTimeOffset.UtcNow);
        }

        public void Pause() => Pauses++;

        public void Resume() => Resumes++;

        public void Stop() => Stops++;

        public void Tick() { }

        public void Restore() { }

        public void RaiseChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class MutableTimeProvider(string utcNow) : TimeProvider
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse(utcNow);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
