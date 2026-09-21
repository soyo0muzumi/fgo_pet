namespace FgoPet.App.Panels;

public sealed class DialogueItemViewModel
{
    public DialogueItemViewModel(string text) => Text = text;

    public string Text { get; }
}