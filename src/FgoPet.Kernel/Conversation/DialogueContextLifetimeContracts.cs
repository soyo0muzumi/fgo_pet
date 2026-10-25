namespace FgoPet.Core.Dialogue;

public interface IDialogueContextLifetime
{
    long Generation { get; }
    Task InvalidateAsync(CancellationToken cancellationToken);
    Task<IDisposable> SuspendAsync(CancellationToken cancellationToken);
    IDialogueBackgroundLease? TryAcquireBackground(CancellationToken cancellationToken) => null;
}

public interface IDialogueBackgroundLease : IDisposable
{
    CancellationToken Token { get; }
    void CheckCurrent();
    T Commit<T>(Func<T> action);
}
