namespace FgoPet.Core.Dialogue;

public interface IDialogueContextLifetime
{
    long Generation { get; }
    Task InvalidateAsync(CancellationToken cancellationToken);
    Task<IDisposable> SuspendAsync(CancellationToken cancellationToken);
}
