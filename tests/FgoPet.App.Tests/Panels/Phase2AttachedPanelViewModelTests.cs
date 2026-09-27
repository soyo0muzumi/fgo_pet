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
using FgoPet.Plugin.Focus.Desktop;

namespace FgoPet.App.Tests.Panels;

public sealed class Phase2AttachedPanelViewModelTests
{
    [Fact]
    public void Countdown_ticks_update_owned_content_without_rearranging_the_shell_and_disposal_detaches()
    {
        var visibilityChanges = 0;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AttachedPanelViewModel.IsCompactSurfaceActive)) visibilityChanges++;
        };
        _focus.Current = FocusingWithRemaining(1_458);
        _focus.RaiseChanged();
        _focus.Current = _focus.Current with { RemainingSeconds = 1_457 };
        _focus.RaiseChanged();
        Assert.Equal("24:17", _compact.FocusDisplay.Remaining);
        Assert.Equal(1, visibilityChanges);
        Assert.Equal(1, _focus.SnapshotSubscriptions);
        _compact.Dispose();
        Assert.Equal(0, _focus.SnapshotSubscriptions);
        _focus.Current = FocusSession.Idle;
        _focus.RaiseChanged();
        Assert.Equal(1, visibilityChanges);
    }

    [Fact]
    public void Process_stop_fences_compact_commands_and_view_construction()
    {
        using var stopping = new CancellationTokenSource();
        using var compact = new FocusCompactViewModel(_focus, stopping: stopping.Token);
        compact.SetActiveServant("servant-mash");
        stopping.Cancel();
        compact.StartFocus();
        compact.PauseTimer();
        compact.ResumeTimer();
        compact.StopTimer();
        Assert.False(compact.CanStartFocus);
        Assert.Null(_focus.StartedPreset);
        Assert.Equal(0, _focus.Pauses + _focus.Resumes + _focus.Stops);
        Assert.Throws<OperationCanceledException>(() => compact.CreateView());
    }
    private const string Epoch = "2026-08-27T09:00:00Z";

    private readonly FakeFocusService _focus;
    private readonly AttachedPanelViewModel _vm;
    private readonly FocusCompactViewModel _compact;

    public Phase2AttachedPanelViewModelTests()
    {
        _focus = new FakeFocusService();
        _compact = new FocusCompactViewModel(_focus);
        _vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), _compact);
    }

    [Fact]
    public void Running_focus_replaces_compact_message_with_timer_without_expanding()
    {
        _vm.PortraitClick();
        _focus.Current = FocusingWithRemaining(1_458);
        _focus.RaiseChanged();

        Assert.Equal(AttachedPanelState.Compact, _vm.State);
        Assert.True(_vm.IsCompactSurfaceActive);
        Assert.Equal("24:18", _compact.FocusDisplay.Remaining);
        Assert.Equal("第 1 / 4 轮", _compact.FocusDisplay.Cycle);
        Assert.Equal(2.8, _compact.ProgressPercent, 1);
    }

    [Fact]
    public void Active_role_state_enables_focus_without_a_role_library_event()
    {
        var runtime = new AppRuntime();
        using var compact = new FocusCompactViewModel(_focus, runtime);
        using var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), compact, runtime: runtime);

        runtime.SetActiveRole(new ActiveRoleState("pack", "casual", "1.0.0", "servant-mash"));

        Assert.True(compact.CanStartFocus);
        Assert.Equal("servant-mash", vm.ActiveServantId);
    }

    [Fact]
    public void Custom_summary_excludes_the_break_after_the_last_cycle()
    {
        _compact.SelectCustomPreset();
        _compact.CustomFocusMinutesText = "35";
        _compact.CustomBreakMinutesText = "10";
        _compact.CustomCyclesText = "3";

        Assert.Equal("02:05:00", _compact.CustomTotalText);
    }

    [Fact]
    public void Custom_step_controls_use_approved_steps_and_clamp_to_bounds()
    {
        _compact.SelectCustomPreset();
        _compact.CustomFocusMinutesText = "178";
        _compact.CustomBreakMinutesText = "1";
        _compact.CustomCyclesText = "12";

        _compact.AdjustCustomFocus(1);
        _compact.AdjustCustomBreak(-1);
        _compact.AdjustCustomCycles(1);

        Assert.Equal("180", _compact.CustomFocusMinutesText);
        Assert.Equal("1", _compact.CustomBreakMinutesText);
        Assert.Equal("12", _compact.CustomCyclesText);
    }

    [Fact]
    public void Paused_timer_shows_the_paused_phase_label()
    {
        _vm.PortraitClick();
        _focus.Current = FocusingWithRemaining(1_458).RestorePaused();
        _focus.RaiseChanged();

        Assert.True(_vm.IsCompactSurfaceActive);
        Assert.Equal("24:18", _compact.FocusDisplay.Remaining);
        // 原为 Assert.True(_vm.IsPaused)：IsPaused 是相位文案的内部中间量，没有 XAML 绑定它。
        // 断言迁到用户真正看到的那条字符串上，锁的是行为而不是实现细节（任务卡 C 步骤 1）。
        Assert.Equal("已暂停", _compact.FocusDisplay.Phase);
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

        Assert.True(_vm.IsCompactSurfaceActive);
        Assert.Contains("休息", _compact.FocusDisplay.Phase);
    }

    [Fact]
    public void Idle_session_shows_the_character_message_not_the_timer()
    {
        _vm.PortraitClick();
        _focus.Current = FocusSession.Idle;
        _focus.RaiseChanged();

        Assert.False(_vm.IsCompactSurfaceActive);
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
    public void FocusClick_from_expanded_todo_switches_to_focus()
    {
        _vm.PortraitClick();
        _vm.TodoClick();
        _vm.FocusClick();

        Assert.Equal(AttachedPanelState.ExpandedFocus, _vm.State);
    }

    [Fact]
    public void Invalid_custom_minutes_disable_start_and_suppress_idle_collapse()
    {
        _vm.PortraitClick();
        _vm.FocusClick();
        _compact.SelectCustomPreset();
        _compact.CustomFocusMinutesText = "4";

        Assert.False(_compact.CanStartFocus);
        Assert.True(_compact.IsEditingCustomPreset);
        Assert.NotEmpty(_compact.CustomFocusError);
    }

    [Fact]
    public void Valid_custom_minutes_enable_start_and_clear_the_error()
    {
        _compact.SetActiveServant("servant-mash");
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _compact.SelectCustomPreset();
        _compact.CustomFocusMinutesText = "45";
        _compact.CustomBreakMinutesText = "9";
        _compact.CustomCyclesText = "3";

        Assert.True(_compact.CanStartFocus);
        Assert.Empty(_compact.CustomFocusError);
        Assert.Empty(_compact.CustomBreakError);
        Assert.Empty(_compact.CustomCyclesError);
    }

    [Fact]
    public void Start_focus_uses_the_selected_builtin_preset_and_current_servant()
    {
        _compact.SetActiveServant("servant-mash");
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _compact.SelectPreset(FocusPresetCatalog.Short);
        _compact.StartFocus();

        Assert.Equal(FocusPresetCatalog.Short, _focus.StartedPreset);
        Assert.Equal("servant-mash", _focus.StartedServantId);
    }

    [Fact]
    public void Timer_commands_forward_to_the_focus_service()
    {
        _focus.Current = FocusingWithRemaining(1_458).RestorePaused();
        _focus.RaiseChanged();

        _compact.PauseTimer();
        _compact.ResumeTimer();
        _compact.StopTimer();

        Assert.Equal(1, _focus.Pauses);
        Assert.Equal(1, _focus.Resumes);
        Assert.Equal(1, _focus.Stops);
    }

    [Fact]
    public void Servant_change_during_editing_keeps_the_panel_open_but_updates_the_owner()
    {
        _vm.PortraitClick();
        _vm.FocusClick();
        _compact.SelectCustomPreset();
        _compact.CustomFocusMinutesText = "4";

        _vm.SetActiveServant("servant-other");

        Assert.Equal(AttachedPanelState.ExpandedFocus, _vm.State);
        Assert.Equal("servant-other", _vm.ActiveServantId);
    }

    [Fact]
    public void Starting_from_the_focus_column_steps_down_to_compact_and_shows_the_timer()
    {
        _compact.SetActiveServant("servant-mash");
        _vm.SetActiveServant("servant-mash");
        _vm.PortraitClick();
        _vm.FocusClick();
        _compact.SelectPreset(FocusPresetCatalog.Short);
        _compact.StartFocus();
        _focus.RaiseChanged();

        Assert.Equal(AttachedPanelState.Compact, _vm.State);
        Assert.True(_vm.IsCompactSurfaceActive);
    }

    [Fact]
    public void Servant_resolution_is_required_before_start_is_enabled()
    {
        _vm.PortraitClick();
        _vm.FocusClick();

        Assert.False(_compact.CanStartFocus);

        _compact.SetActiveServant("servant-mash");
        _vm.SetActiveServant("servant-mash");
        Assert.True(_compact.CanStartFocus);
    }

    private static FocusSession FocusingWithRemaining(int remaining) => FocusSession.Start(
        "session-1", "servant-mash", FocusPreset.Create(25, 5, 4),
        DateTimeOffset.Parse(Epoch)) with { RemainingSeconds = remaining };

    private sealed class FakeFocusService : IFocusSessionService
    {
        public FocusSession Current { get; set; } = FocusSession.Idle;

        public event EventHandler? SnapshotChanged;
        public int SnapshotSubscriptions => SnapshotChanged?.GetInvocationList().Length ?? 0;

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
