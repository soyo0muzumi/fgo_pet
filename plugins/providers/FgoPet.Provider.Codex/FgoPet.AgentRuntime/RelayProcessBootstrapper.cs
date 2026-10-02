using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
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

        cancellationToken.ThrowIfCancellationRequested();
        var start = _timeProvider.GetTimestamp();
        using var budget = new CancellationTokenSource(options.StartupTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var token = linked.Token;
        var launched = false;
        string? lastError = null;
        TimeSpan Remaining() => options.StartupTimeout - _timeProvider.GetElapsedTime(start);
        RelayBootstrapResult TimedOut() => new(RelayBootstrapStatus.TimedOut, lastError ?? "Relay startup timed out.");
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) return TimedOut();
                // Bound even a faulty injected probe that ignores its cancellation token.
                var result = await _probe.ProbeAsync(options, token)
                    .WaitAsync(remaining, _timeProvider, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (Remaining() <= TimeSpan.Zero) return TimedOut();
                lastError = result.Error ?? lastError;
                if (IsVersionMismatch(result) || result.Ready && !IsCurrentProtocol(result))
                {
                    return new RelayBootstrapResult(RelayBootstrapStatus.VersionMismatch, result.Error ?? DescribeVersion(result.ProtocolVersion));
                }

                if (result.Ready) return new RelayBootstrapResult(RelayBootstrapStatus.Ready, null);
                if (!launched)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        _launcher.Start(options);
                        launched = true;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        return new RelayBootstrapResult(RelayBootstrapStatus.StartFailed, "relay_start_failed");
                    }
                }

                remaining = Remaining();
                if (remaining <= TimeSpan.Zero) return TimedOut();
                await _delay.DelayAsync(remaining < ProbeInterval ? remaining : ProbeInterval, token)
                    .WaitAsync(remaining, _timeProvider, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut();
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TimedOut();
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
        using var process = Process.Start(info);
        if (process is null)
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
        RelayProbeResult? app = null;
        try
        {
            app = await appTask.ConfigureAwait(false);
            if (!app.Ready) return app;
            var adapterReady = await adapterTask.ConfigureAwait(false);
            return adapterReady
                ? app
                : new RelayProbeResult(false, app.ProtocolVersion, "The adapter relay pipe is not ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An unavailable adapter cannot erase an app endpoint's known incompatibility.
            return app is { Ready: false }
                ? app
                : new RelayProbeResult(false, app?.ProtocolVersion, "Relay pipe probe timed out.");
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            // Both connect operations are owned here, including failure/cancellation paths.
            try { await Task.WhenAll(appTask, adapterTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
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
            if (envelope.MessageId != request.MessageId || envelope.MessageType != "connection_test")
                throw new AgentProtocolValidationException("The connection probe response does not match its request.");
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
            // The standalone Relay can pair adapters while the desktop App is closed.
            var ready = response.RelayOnline;
            return new RelayProbeResult(
                ready,
                response.ProtocolVersion,
                ready ? null : response.Error ?? response.Status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or AgentProtocolValidationException
            or JsonException or DecoderFallbackException or UnauthorizedAccessException)
        {
            return new RelayProbeResult(false, null, "The app relay pipe did not return a valid connection response.");
        }
    }

    private static async Task<bool> ProbeAdapterAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
