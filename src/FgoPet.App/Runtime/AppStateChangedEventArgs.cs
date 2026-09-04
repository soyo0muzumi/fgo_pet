namespace FgoPet.App.Runtime;

public sealed class AppStateChangedEventArgs<T>(T state) : EventArgs
{
    public T State { get; } = state;
}
