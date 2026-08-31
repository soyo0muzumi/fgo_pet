namespace FgoPet.AgentRuntime;

/// <summary>Validated paths and timing values used by the relay bootstrap.</summary>
public sealed record RelayRuntimeOptions
{
    public RelayRuntimeOptions(
        string PipeSuffix,
        string StateRoot,
        string RelayExecutablePath,
        TimeSpan ConnectTimeout,
        TimeSpan StartupTimeout)
    {
        Validate(PipeSuffix, StateRoot, RelayExecutablePath, ConnectTimeout, StartupTimeout);
        this.PipeSuffix = PipeSuffix;
        this.StateRoot = Path.GetFullPath(StateRoot);
        this.RelayExecutablePath = Path.GetFullPath(RelayExecutablePath);
        this.ConnectTimeout = ConnectTimeout;
        this.StartupTimeout = StartupTimeout;
    }

    public string PipeSuffix { get; }
    public string StateRoot { get; }
    public string RelayExecutablePath { get; }
    public TimeSpan ConnectTimeout { get; }
    public TimeSpan StartupTimeout { get; }

    public static RelayRuntimeOptions ForCurrentUser()
    {
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FgoPet");
        var relayExecutable = Path.Combine(AppContext.BaseDirectory, "FgoPet.AgentRelay.exe");
        return new RelayRuntimeOptions(
            "v1",
            stateRoot,
            relayExecutable,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(10));
    }

    public static void Validate(
        string pipeSuffix,
        string stateRoot,
        string relayExecutablePath,
        TimeSpan connectTimeout,
        TimeSpan startupTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeSuffix);
        if (pipeSuffix.Length > 64 || pipeSuffix.Any(character =>
                !(character is >= 'A' and <= 'Z')
                && !(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Pipe suffix must contain only letters, numbers, '.', '_' or '-'.", nameof(pipeSuffix));
        }

        ValidateAbsolutePath(stateRoot, nameof(stateRoot));
        ValidateAbsolutePath(relayExecutablePath, nameof(relayExecutablePath));
        if (!string.Equals(Path.GetFileName(relayExecutablePath), "FgoPet.AgentRelay.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Relay executable path must name the sibling FgoPet.AgentRelay.exe.", nameof(relayExecutablePath));
        }

        ValidateTimeout(connectTimeout, nameof(connectTimeout));
        ValidateTimeout(startupTimeout, nameof(startupTimeout));
    }

    private static void ValidateAbsolutePath(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be absolute; arbitrary PATH lookup is not allowed.", name);
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The path is not valid.", name, error);
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, "Timeout must be finite and positive.");
        }
    }
}
