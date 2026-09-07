using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationTurnViewModel : ObservableObject
{
    private static readonly Brush AssistantBrush = new SolidColorBrush(Color.FromRgb(0x70, 0xE7, 0xF5));
    private static readonly Brush UserBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x42, 0xE8));
    private static readonly Brush AssistantBubbleBackground = Brushes.Transparent;
    private static readonly Brush UserBubbleBackground = new SolidColorBrush(Color.FromArgb(0x1F, 0xD2, 0x42, 0xE8));

    static ConversationTurnViewModel()
    {
        AssistantBrush.Freeze();
        UserBrush.Freeze();
        UserBubbleBackground.Freeze();
    }

    public ConversationTurnViewModel(string messageId, ChatMessageRole role, string text, bool isStreaming = false)
    {
        MessageId = messageId;
        Role = role;
        Text = text;
        IsStreaming = isStreaming;
    }

    public string MessageId { get; }
    public ChatMessageRole Role { get; }
    public bool IsAssistant => Role == ChatMessageRole.Assistant;
    public string RoleLabel => Role == ChatMessageRole.User ? "MASTER / 我" : "SERVANT / 从者";
    public bool HasVisibleReasoning => IsAssistant && ReasoningText.Length > 0;
    public Brush RoleBrush => Role == ChatMessageRole.User ? UserBrush : AssistantBrush;
    public HorizontalAlignment Alignment =>
        Role == ChatMessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush BubbleBackground =>
        Role == ChatMessageRole.User ? UserBubbleBackground : AssistantBubbleBackground;
    public Brush BubbleBorderBrush => RoleBrush;
    public Thickness BubbleBorderThickness => new(
        Role == ChatMessageRole.User ? 1 : 2,
        Role == ChatMessageRole.User ? 1 : 0,
        Role == ChatMessageRole.User ? 1 : 0,
        Role == ChatMessageRole.User ? 1 : 0);

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>Transient reasoning display (spec §9): never persisted or exported.</summary>
    [ObservableProperty]
    private string _reasoningText = string.Empty;

    /// <summary>Dynamic one-line summary supplied by the request state/adapter layer.</summary>
    [ObservableProperty]
    private string _reasoningSummary = string.Empty;

    /// <summary>True until the first content delta arrives; the well header shows the live timer.</summary>
    [ObservableProperty]
    private bool _isThinkingActive;

    /// <summary>Frozen "思考 · N.Ns" label captured when the first content delta lands.</summary>
    [ObservableProperty]
    private string _reasoningDurationText = string.Empty;

    /// <summary>Well collapsed after the first content delta; user can re-expand.</summary>
    [ObservableProperty]
    private bool _isReasoningExpanded = true;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Text += text;
        if (IsThinkingActive)
        {
            IsThinkingActive = false;
            IsReasoningExpanded = false;
        }
    }

    public void AppendReasoning(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ReasoningText += text;
    }

    partial void OnReasoningTextChanged(string value) => OnPropertyChanged(nameof(HasVisibleReasoning));

    public void SetReasoningSummary(string text) => ReasoningSummary = text?.Trim() ?? string.Empty;
}
