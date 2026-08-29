using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationTurnViewModel : ObservableObject
{
    public ConversationTurnViewModel(string messageId, ChatMessageRole role, string text, bool isStreaming = false)
    {
        MessageId = messageId;
        Role = role;
        Text = text;
        IsStreaming = isStreaming;
    }

    public string MessageId { get; }
    public ChatMessageRole Role { get; }
    public string RoleLabel => Role == ChatMessageRole.User ? "我" : "从者";

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
