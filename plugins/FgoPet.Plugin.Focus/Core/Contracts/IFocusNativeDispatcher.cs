namespace FgoPet.App.Focus;

/// <summary>Marshals Focus owner access onto its serialized application dispatcher.</summary>
public interface IFocusNativeDispatcher
{
    T Invoke<T>(Func<T> operation);

    ValueTask<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}
