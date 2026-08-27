using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public sealed class AppStartup
{
    private readonly ILogger<AppStartup> _logger;

    public AppStartup(ILogger<AppStartup> logger) => _logger = logger;

    public void Start()
    {
        // Packless startup, portrait windows, and the tray are wired in Task 5/10.
        _logger.LogInformation("FgoPet host started.");
    }
}