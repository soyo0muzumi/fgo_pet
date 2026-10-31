namespace FgoPet.Extensibility;

/// <summary>A local readiness hint; it neither performs network I/O nor grants execution authority.</summary>
public sealed record ToolAvailability(bool IsAvailable, string? ErrorCode = null);

/// <summary>Optional, live preflight information for tools with user-configured dependencies.</summary>
public interface IToolAvailabilityProvider
{
    ToolAvailability GetAvailability();
}
