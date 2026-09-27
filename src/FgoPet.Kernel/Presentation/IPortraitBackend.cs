using FgoPet.Core.Portraits;

namespace FgoPet.Kernel.Presentation;

/// <summary>A selected rendering backend accepts semantic actions without exposing SDK parameters to the Kernel.</summary>
public interface IPortraitBackend : IPortraitController
{
    string BackendId { get; }
}
