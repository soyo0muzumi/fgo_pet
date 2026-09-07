using FgoPet.App.Dialogue;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Focus;
using FgoPet.Core.Packs;
using FgoPet.Core.Panels;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Providers;
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
    public void Dialogue_click_requests_the_standalone_window_without_changing_panel_state()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), focus: null, dialogueWindow: CreateDialogueViewModel());
        vm.PortraitClick();
        Assert.Equal(AttachedPanelState.Compact, vm.State);

        var requested = 0;
        vm.DialogueWindow!.OpenRequested += () => requested++;
        vm.DialogueClick();

        Assert.Equal(1, requested);
        Assert.Equal(AttachedPanelState.Compact, vm.State);
    }

    [Fact]
    public void Unread_replies_surface_on_the_compact_panel_and_clear_when_activated()
    {
        var dialogue = CreateDialogueViewModel();
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), focus: null, dialogueWindow: dialogue);

        dialogue.NotifyWindowHidden();
        dialogue.Conversation.Turns.Add(new FgoPet.App.Dialogue.ConversationTurnViewModel(
            "m1", FgoPet.Core.Dialogue.ChatMessageRole.Assistant, "这是一条新的回复。"));

        Assert.Equal(1, vm.DialogueUnreadCount);
        Assert.Equal("对话 · 新回复：这是一条新的回复。", vm.DialogueUnreadPillText);

        dialogue.NotifyActivated();

        Assert.Equal(0, vm.DialogueUnreadCount);
        Assert.Equal(string.Empty, vm.DialogueUnreadPillText);
    }

    private static DialogueWindowViewModel CreateDialogueViewModel()
    {
        var settingsStore = new MemorySettingsStore(AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        });
        var database = new RuntimeDatabase(":memory:");
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new SqliteConversationRepository(database),
            new SqliteMemoryRepository(database),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new DialogueWindowViewModel(new ConversationViewModel(orchestrator, settingsStore));
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
        vm.TodoClick();
        Assert.Equal(AttachedPanelState.ExpandedTodo, vm.State);

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
        vm.TodoClick();

        vm.PointerEntered();
        time.Now = time.Now.AddMinutes(1);
        vm.Tick();

        Assert.Equal(AttachedPanelState.ExpandedTodo, vm.State);
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

    [Fact]
    public void Start_focus_is_disabled_without_an_active_servant_and_names_the_reason()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), new FakeFocusService(FocusSession.Idle));

        Assert.False(vm.CanStartFocus);
        Assert.Equal("请先在角色库导入并激活一个角色。", vm.StartFocusDisabledReason);
    }

    [Fact]
    public void Start_focus_is_disabled_by_an_active_session_and_names_the_status()
    {
        var session = FocusSession.Idle with { Status = FocusStatus.PausedFocus };
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), new FakeFocusService(session));
        vm.SetActiveServant("800100");

        Assert.False(vm.CanStartFocus);
        Assert.Equal("当前专注处于 暂停（专注），请先恢复或退出后再开始新专注。", vm.StartFocusDisabledReason);
    }

    [Fact]
    public void Start_focus_is_disabled_by_invalid_custom_fields_and_names_the_correction()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), new FakeFocusService(FocusSession.Idle));
        vm.SetActiveServant("800100");
        vm.SelectCustomPreset();
        vm.CustomFocusMinutesText = "999";

        Assert.True(vm.IsEditingCustomPreset);
        Assert.False(vm.CanStartFocus);
        Assert.Equal("请先修正自定义专注/休息/轮次设置在允许范围内。", vm.StartFocusDisabledReason);
    }

    [Fact]
    public void Start_focus_enabled_state_has_no_disabled_reason()
    {
        var vm = new AttachedPanelViewModel(new MutableTimeProvider(Epoch), new FakeFocusService(FocusSession.Idle));
        vm.SetActiveServant("800100");

        Assert.True(vm.CanStartFocus);
        Assert.Null(vm.StartFocusDisabledReason);
    }

    private sealed class FakeFocusService(FocusSession session) : IFocusSessionService
    {
        public FocusSession Current { get; private set; } = session;

        // A no-op subscriber keeps the compiler happy about unused events.
        public event EventHandler? SnapshotChanged { add { } remove { } }

        public event EventHandler? PersistenceFailed { add { } remove { } }

        public void Start(FocusPreset preset, string servantId) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void Tick() { }
        public void Restore() { }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(string utcNow) => Now = DateTimeOffset.Parse(utcNow);

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemorySettingsStore(AppSettings initial) : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => initial;
        public void Save(AppSettings settings) { }
    }

    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }
}