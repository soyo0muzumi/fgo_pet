using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.AgentRuntime;

public enum RelayBootstrapStatus
{
    Ready,
    StartFailed,
    TimedOut,
    VersionMismatch,
}

public sealed record RelayBootstrapResult(RelayBootstrapStatus Status, string? Error);

public sealed record RelayProbeResult(bool Ready, string? ProtocolVersion, string? Error);

public interface IRelayProbe
{
    Task<RelayProbeResult> ProbeAsync(RelayRuntimeOptions options, CancellationToken cancellationToken);
}

public interface IRelayProcessLauncher
{
    void Start(RelayRuntimeOptions options);
}

public interface IRuntimeDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class DefaultRuntimeDelay : IRuntimeDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class RelayProcessBootstrapper
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);
    private readonly IRelayProbe _probe;
    private readonly IRelayProcessLauncher _launcher;
    private readonly IRuntimeDelay _delay;
    private readonly TimeProvider _timeProvider;

    public RelayProcessBootstrapper(
        IRelayProbe probe,
        IRelayProcessLauncher launcher,
        IRuntimeDelay delay,
        TimeProvider? timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RelayBootstrapResult> EnsureReadyAsync(
        RelayRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        RelayRuntimeOptions.Validate(
            options.PipeSuffix,
            options.StateRoot,
            options.RelayExecutablePath,
            options.ConnectTimeout,
            options.StartupTimeout);

        var first = await _probe.ProbeAsync(options, cancellationToken).ConfigureAwait(false);
        if (IsVersionMismatch(first))
        {
            return new RelayBootstrapResult(RelayBootstrapStatus.VersionMismatch, first.Error ?? DescribeVersion(first.ProtocolVersion));
        }

        if (first.Ready && IsCurrentProtocol(first))
        {
            return new RelayBootstrapResult(RelayBootstrapStatus.Ready, null);
        }

        if (first.Ready)
        {
            return new RelayBootstrapResult(RelayBootstrapStatus.VersionMismatch, first.Error ?? DescribeVersion(first.ProtocolVersion));
        }

        try
        {
            _launcher.Start(options);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new RelayBootstrapResult(RelayBootstrapStatus.StartFailed, error.Message);
        }

        var start = _timeProvider.GetTimestamp();
        var lastError = first.Error;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = _timeProvider.GetElapsedTime(start);
            if (elapsed >= options.StartupTimeout)
            {
                return new RelayBootstrapResult(RelayBootstrapStatus.TimedOut, lastError ?? "Relay startup timed out.");
            }

            var remaining = options.StartupTimeout - elapsed;
            await _delay.DelayAsync(remaining < ProbeInterval ? remaining : ProbeInterval, cancellationToken)
                .ConfigureAwait(false);

            var result = await _probe.ProbeAsync(options, cancellationToken).ConfigureAwait(false);
            lastError = result.Error ?? lastError;
            if (IsVersionMismatch(result))
            {
                return new RelayBootstrapResult(RelayBootstrapStatus.VersionMismatch, result.Error ?? DescribeVersion(result.ProtocolVersion));
            }

            if (result.Ready && IsCurrentProtocol(result))
            {
                return new RelayBootstrapResult(RelayBootstrapStatus.Ready, null);
            }

            if (result.Ready)
            {
                return new RelayBootstrapResult(RelayBootstrapStatus.VersionMismatch, result.Error ?? DescribeVersion(result.ProtocolVersion));
            }
        }
    }

    private static bool IsVersionMismatch(RelayProbeResult result) =>
        result.ProtocolVersion is not null
        && !string.Equals(result.ProtocolVersion, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal);

    private static bool IsCurrentProtocol(RelayProbeResult result) =>
        string.Equals(result.ProtocolVersion, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal);

    private static string DescribeVersion(string? version) =>
        $"Unsupported relay protocol version '{version ?? "unknown"}'.";
}

public sealed class DefaultRelayProcessLauncher : IRelayProcessLauncher
{
    public void Start(RelayRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RelayRuntimeOptions.Validate(
            options.PipeSuffix,
            options.StateRoot,
            options.RelayExecutablePath,
            options.ConnectTimeout,
            options.StartupTimeout);
        Directory.CreateDirectory(options.StateRoot);
        var info = new ProcessStartInfo
        {
            FileName = options.RelayExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.RelayExecutablePath)!,
        };
        info.ArgumentList.Add("--pipe-suffix");
        info.ArgumentList.Add(options.PipeSuffix);
        info.ArgumentList.Add("--state-root");
        info.ArgumentList.Add(options.StateRoot);
        if (Process.Start(info) is null)
        {
            throw new InvalidOperationException("The relay process could not be started.");
        }
    }
}

public sealed class DefaultRelayProbe : IRelayProbe
{
    public async Task<RelayProbeResult> ProbeAsync(RelayRuntimeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        RelayRuntimeOptions.Validate(
            options.PipeSuffix,
            options.StateRoot,
            options.RelayExecutablePath,
            options.ConnectTimeout,
            options.StartupTimeout);
        var names = RelayPipeNames.ForCurrentUser(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);

        var appTask = ProbeAppAsync(names.App, options.ConnectTimeout, timeout.Token);
        var adapterTask = ProbeAdapterAsync(names.Adapter, options.ConnectTimeout, timeout.Token);
        RelayProbeResult app;
        bool adapterReady;
        try
        {
            app = await appTask.ConfigureAwait(false);
            adapterReady = await adapterTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RelayProbeResult(false, null, "Relay pipe probe timed out.");
        }

        if (!app.Ready)
        {
            return app with { Ready = false };
        }

        return adapterReady
            ? app
            : new RelayProbeResult(false, app.ProtocolVersion, "The adapter relay pipe is not ready.");
    }

    private static async Task<RelayProbeResult> ProbeAppAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var client = new JsonLinePipeClient(pipeName, timeout);
            var request = ProtocolEnvelope.Create(
                "connection-test-" + Guid.NewGuid().ToString("N"),
                "connection_test",
                new { });
            var line = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var envelope = ProtocolEnvelope.Parse(line);
            if (!string.Equals(envelope.ProtocolVersion, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal))
            {
                return new RelayProbeResult(false, envelope.ProtocolVersion, "The relay protocol version is not supported.");
            }

            var response = envelope.DeserializePayload<RelayConnectionTestResponse>();
            if (!string.Equals(response.ProtocolVersion, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal))
            {
                return new RelayProbeResult(false, response.ProtocolVersion, "The relay protocol version is not supported.");
            }

            AgentProtocolValidator.ValidateResponse(envelope);
            var ready = response.RelayOnline && response.AppOnline;
            return new RelayProbeResult(
                ready,
                response.ProtocolVersion,
                ready ? null : response.Error ?? response.Status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or AgentProtocolValidationException or JsonException)
        {
            return new RelayProbeResult(false, null, "The app relay pipe did not return a valid connection response.");
        }
    }

    private static async Task<bool> ProbeAdapterAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
