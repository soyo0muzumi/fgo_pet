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
    public string RoleLabel => Role == ChatMessageRole.User ? "MASTER / 我" : "SERVANT / 从者";
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

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Text += text;
    }
}
