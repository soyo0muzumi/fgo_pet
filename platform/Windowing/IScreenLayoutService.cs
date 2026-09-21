using FgoPet.Core.Geometry;

namespace FgoPet.Core.Windowing;

/// <summary>Windows monitor abstraction so restoration logic stays unit-testable.</summary>
public interface IScreenLayoutService
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    Dpi2 GetDpi(string monitorId);
}