using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.UiSdk;
using FgoPet.App.Dialogue;
using FgoPet.Core.Panels;
using FgoPet.App.Runtime;
using FgoPet.App.Settings;
using FgoPet.Speech.Settings;
using FgoPet.Kernel.Companion;

namespace FgoPet.App.Panels;

/// <summary>Owns bounded entry-shell lists, navigation and idle container state. Feature content owns its editing and activity.</summary>
public sealed partial class AttachedPanelViewModel : ObservableObject, IAttachedPanelLauncher, IDisposable
{
    public const int DialogueCapacity = 20;
    public const int DialogueVisible = 6;

    private readonly TimeProvider _time;
    private readonly AppRuntime? _runtime;
    private readonly Dispatcher _dispatcher;
    private readonly ISpeechSettingsStore? _settings;
    private readonly CompanionPresentation? _presentation;
    private readonly Func<string, bool>? _toggleTransient;
    private bool _disposed;
    private DateTimeOffset _lastInteraction;
    private bool _pointerInside;
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);

    public AttachedPanelViewModel(TimeProvider time) : this(time, compactSurface: null, conversation: null)
    {
    }

    public AttachedPanelViewModel(
        TimeProvider time,
        ICompactSurface? compactSurface,
        ConversationViewModel? conversation = null,
        AppRuntime? runtime = null,
        DialogueWindowViewModel? dialogueWindow = null,
        ISpeechSettingsStore? settings = null,
        CompanionPresentation? presentation = null,
        Func<string, bool>? toggleTransient = null)
    {
        _time = time;
        CompactSurface = compactSurface;
        _lastSurfaceActive = compactSurface?.IsActive == true;
        Conversation = conversation;
        _runtime = runtime;
        DialogueWindow = dialogueWindow;
        _settings = settings;
        _presentation = presentation;
        _toggleTransient = toggleTransient;
        _isAutoReadEnabled = settings?.Load().Connection.AutoReadEnabled ?? false;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _lastInteraction = time.GetUtcNow();
        if (dialogueWindow is not null)
        {
            dialogueWindow.UnreadChanged += RefreshUnread;
        }
        if (_runtime?.ActiveRole is { } activeRole)
        {
            _activeServantId = activeRole.ServantId;
        }
        if (_runtime is not null)
        {
            _runtime.ActiveRoleChanged += OnActiveRoleChanged;
        }
        if (compactSurface is not null)
        {
            compactSurface.PropertyChanged += OnCompactSurfaceChanged;
            compactSurface.Interaction += Interact;
        }
        if (presentation is not null) presentation.Changed += OnPresentationChanged;
    }

    /// <summary>Shared dialogue-window state; owns the unread badge this panel renders.</summary>
    public DialogueWindowViewModel? DialogueWindow { get; }

    [ObservableProperty]
    private int _dialogueUnreadCount;

    /// <summary>Pill text on the compact body: last unread reply preview (spec §8.2 R3).</summary>
    [ObservableProperty]
    private string _dialogueUnreadPillText = string.Empty;

    public bool HasDialogueUnread => DialogueUnreadCount > 0;

    public string GreetingText => "今天也按自己的节奏来。";
    public string ChatActionAutomationName => "打开聊天";
    public string ToolsActionAutomationName => "打开更多能力";
    public string AttentionActionAutomationName => "查看需要关注的内容";
    public string SpeechActionAutomationName => IsAutoReadEnabled ? "关闭自动朗读" : "开启自动朗读";
    public string MoreActionAutomationName => "更多";
    public bool IsAutoReadEnabled => _isAutoReadEnabled;
    private bool _isAutoReadEnabled;

    public event Action<SettingsSection>? SettingsRequested;
    public event Action? ExitRequested;

    private void RefreshUnread()
    {
        var dialogue = DialogueWindow;
        if (dialogue is null)
        {
            return;
        }

        DialogueUnreadCount = dialogue.UnreadCount;
        OnPropertyChanged(nameof(HasDialogueUnread));
        if (dialogue.UnreadCount == 0)
        {
            DialogueUnreadPillText = string.Empty;
            return;
        }

        var lastReply = dialogue.Conversation.Turns
            .LastOrDefault(turn => turn.Role == Core.Dialogue.ChatMessageRole.Assistant)?.Text;
        DialogueUnreadPillText = $"对话 · 新回复：{Preview(lastReply)}";
    }

    private static string Preview(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "…";
        }

        return trimmed.Length <= 18 ? trimmed : trimmed[..18] + "…";
    }

    [ObservableProperty]
    private AttachedPanelState _state = AttachedPanelState.Collapsed;

    [ObservableProperty]
    private bool _autoCollapseEnabled = true;

    [ObservableProperty]
    private string _activeServantId = string.Empty;

    public ICompactSurface? CompactSurface { get; }
    public bool IsCompactSurfaceActive => CompactSurface?.IsActive == true;
    public bool BlocksAutoCollapse => CompactSurface?.BlocksAutoCollapse == true;
    private bool _lastSurfaceActive;

    public ObservableCollection<DialogueItemViewModel> Dialogue { get; } = new();

    private ConversationViewModel? _conversation;

    public ConversationViewModel? Conversation
    {
        get => _conversation;
        set
        {
            if (!ReferenceEquals(_conversation, value))
            {
                _conversation = value;
                OnPropertyChanged(nameof(Conversation));
            }
        }
    }

    public int VisibleDialogueCount => Math.Min(Dialogue.Count, DialogueVisible);

    public void PortraitClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.PortraitClick);
    }

    public void FocusClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.FocusClick);
    }

    public void TodayClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.TodayClick);
    }

    public void DialogueClick()
    {
        Interact();
        DialogueWindow?.RequestOpen();
    }

    public void ToggleAutoRead()
    {
        Interact();
        if (_settings is null)
        {
            SettingsRequested?.Invoke(SettingsSection.Speech);
            return;
        }

        var current = _settings.Load();
        _isAutoReadEnabled = !current.Connection.AutoReadEnabled;
        _settings.Save(current with
        {
            Connection = current.Connection with { AutoReadEnabled = _isAutoReadEnabled },
        });
        OnPropertyChanged(nameof(IsAutoReadEnabled));
        OnPropertyChanged(nameof(SpeechActionAutomationName));
    }

    public void RequestSettings(SettingsSection section = SettingsSection.Personalization)
    {
        Interact();
        SettingsRequested?.Invoke(section);
    }

    public void RequestExit()
    {
        Interact();
        ExitRequested?.Invoke();
    }

    public void OpenTasks()
    {
        Interact();
        if (_toggleTransient?.Invoke("todo.peek") == true) return;
        TodoClick();
    }

    public void AttentionClick()
    {
        Interact();
        if (HasDialogueUnread)
        {
            DialogueWindow?.RequestOpen();
            return;
        }

    }

    public void TodoClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.TodoClick);
    }

    public void Escape()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.Escape);
    }

    public void SetActiveServant(string servantId)
    {
        ActiveServantId = servantId;
        Conversation?.SetActiveServant(servantId);
    }

    private void OnActiveRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && ReferenceEquals(_runtime?.ActiveRole, args.State)) SetActiveServant(args.State.ServantId);
            });
            return;
        }

        SetActiveServant(args.State.ServantId);
    }

    private void OnPresentationChanged(CompanionPresentationState? state)
    {
        if (_disposed || state?.Text is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _presentation?.IsCurrent(state) == true) AddDialogue(state.Text);
        });
    }

    private void OnCompactSurfaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => OnCompactSurfaceChanged(sender, args));
            return;
        }
        if (args.PropertyName == nameof(ICompactSurface.IsActive))
        {
            var active = IsCompactSurfaceActive;
            if (_lastSurfaceActive == active) return;
            _lastSurfaceActive = active;
            if (active && AttachedPanelStateMachine.IsExpanded(State)) State = AttachedPanelState.Compact;
            OnPropertyChanged(nameof(IsCompactSurfaceActive));
        }
        if (args.PropertyName == nameof(ICompactSurface.BlocksAutoCollapse)) OnPropertyChanged(nameof(BlocksAutoCollapse));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is not null) _runtime.ActiveRoleChanged -= OnActiveRoleChanged;
        if (CompactSurface is not null)
        {
            CompactSurface.PropertyChanged -= OnCompactSurfaceChanged;
            CompactSurface.Interaction -= Interact;
        }
        if (_presentation is not null) _presentation.Changed -= OnPresentationChanged;
        if (DialogueWindow is not null) DialogueWindow.UnreadChanged -= RefreshUnread;
    }

    public void AddDialogue(string text)
    {
        Interact();
        Dialogue.Add(new DialogueItemViewModel(text));
        while (Dialogue.Count > DialogueCapacity)
        {
            Dialogue.RemoveAt(0);
        }

        OnPropertyChanged(nameof(VisibleDialogueCount));
    }

    public void PointerEntered() => _pointerInside = true;

    public void PointerLeft() => _pointerInside = false;

    /// <summary>Periodic tick that applies the 30-second idle collapse.</summary>
    public void Tick()
    {
        var next = AttachedPanelStateMachine.ApplyIdle(
            State,
            _time,
            _lastInteraction,
            _idleTimeout,
            AutoCollapseEnabled,
            !_pointerInside,
            BlocksAutoCollapse);
        if (next != State)
        {
            State = next;
        }
    }

    private void Interact() => _lastInteraction = _time.GetUtcNow();

}
