namespace FgoPet.Core.Portraits;

/// <summary>Drives two-phase portrait activation and expression/scale changes.</summary>
public interface IPortraitController
{
    Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken);

    void SetExpression(ExpressionSemantic semantic);

    void SetScale(double scale);
}