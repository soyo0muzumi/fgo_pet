using FgoPet.Core.Geometry;

namespace FgoPet.Core.Windowing;

/// <summary>
/// The read-only Alpha-mask surface that transparent hit testing needs, and nothing else.
/// </summary>
/// <remarks>
/// R8 in findings/35-split-path-blockers.md: the windowing hit-test service used to take a
/// concrete <c>PortraitSnapshot</c>, which pinned <c>host/DesktopShell/Infrastructure</c> to
/// the character module's implementation. Depending on this interface instead keeps the
/// consumer depending on a platform abstraction, while the provider stays free to evolve.
/// </remarks>
public interface IAlphaMaskSource
{
    /// <summary>Source-pixel geometry of the body and overlay layers.</summary>
    PortraitSourceGeometry SourceGeometry { get; }

    /// <summary>Stable id of the asset that defines the body silhouette.</summary>
    string BodyId { get; }

    /// <summary>Precomputed 8-bit Alpha masks, keyed by asset stable id.</summary>
    IReadOnlyDictionary<string, byte[]> AlphaMasks { get; }
}
