namespace FgoPet.App.Panels;

public sealed class TodoItemViewModel
{
    public TodoItemViewModel(string text) => Text = text;

    public string Text { get; }
}