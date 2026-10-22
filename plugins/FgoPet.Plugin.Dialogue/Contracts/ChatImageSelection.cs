namespace FgoPet.Dialogue.Contracts;

/// <summary>One native-picker selection, shared only after Send with the disclosed model.</summary>
public interface IChatImageSelection
{
    bool CanChoose { get; }
    string? Label { get; }
    string Disclosure { get; }
    event EventHandler? Changed;
    Task<bool> ChooseAsync(Func<string?> nativePicker, CancellationToken token);
    Task<bool> ImportAsync(byte[] bytes, string mimeType, CancellationToken token) => Task.FromResult(false);
    void Clear();
}
