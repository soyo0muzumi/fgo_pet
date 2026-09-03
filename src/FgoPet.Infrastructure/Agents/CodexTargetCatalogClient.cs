using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentRuntime;
using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

/// <summary>Queries the shipped Adapter target catalog without crossing the Relay path boundary.</summary>
public sealed class CodexTargetCatalogClient : IAgentTargetCatalog
{
    private const int MaxTargets = 256;
    private const int MaxTextLength = 256;
    private const int MaxStdoutBytes = 1024 * 1024;
    private static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(5);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RelayRuntimeOptions _options;
    private readonly Func<CancellationToken, Task<(int ExitCode, string Stdout)>> _runner;

    public CodexTargetCatalogClient(
        RelayRuntimeOptions options,
        Func<CancellationToken, Task<(int ExitCode, string Stdout)>>? runner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? RunAdapterListAsync;
    }

    public async Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetAdapterPath();
        if (!File.Exists(path))
        {
            return new(AgentTargetCatalogStatus.AdapterNotInstalled, [], "adapter_not_installed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdapterTimeout);
        try
        {
            var result = await _runner(timeout.Token)
                .WaitAsync(AdapterTimeout, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
            {
                return new(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_unavailable");
            }

            return new(AgentTargetCatalogStatus.Available, Parse(result.Stdout));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(AgentTargetCatalogStatus.TimedOut, [], "adapter_timeout");
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            return new(AgentTargetCatalogStatus.TimedOut, [], "adapter_timeout");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or DecoderFallbackException or EncoderFallbackException)
        {
            return new(AgentTargetCatalogStatus.InvalidResponse, [], "adapter_invalid_response");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or ArgumentException)
        {
            return new(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_unavailable");
        }
    }

    public static IReadOnlyList<AgentTargetDescriptor> Parse(string json)
    {
        if (json is null)
        {
            throw InvalidResponse();
        }

        try
        {
            if (Utf8.GetByteCount(json) > MaxStdoutBytes)
            {
                throw InvalidResponse();
            }

            var wireTargets = JsonSerializer.Deserialize<AdapterTarget?[]>(json, JsonOptions);
            if (wireTargets is null || wireTargets.Length == 0 || wireTargets.Length > MaxTargets)
            {
                throw InvalidResponse();
            }

            var targetIds = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<AgentTargetDescriptor>(wireTargets.Length);
            foreach (var wireTarget in wireTargets)
            {
                if (wireTarget is null || wireTarget.ReadOnly is null
                    || string.IsNullOrWhiteSpace(wireTarget.Directory)
                    || wireTarget.Directory.Any(char.IsControl))
                {
                    throw InvalidResponse();
                }

                var targetId = ValidateText(wireTarget.TargetId);
                var displayName = ValidateText(wireTarget.DisplayName);
                if (!targetIds.Add(targetId))
                {
                    throw InvalidResponse();
                }

                targets.Add(new AgentTargetDescriptor(targetId, displayName, wireTarget.ReadOnly.Value));
            }

            return targets;
        }
        catch (JsonException error)
        {
            throw InvalidResponse(error);
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        {
            throw InvalidResponse(error);
        }
    }

    private async Task<(int ExitCode, string Stdout)> RunAdapterListAsync(CancellationToken cancellationToken)
    {
        var path = GetAdapterPath();
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        info.ArgumentList.Add("target");
        info.ArgumentList.Add("list");
        info.Environment.Clear();
        info.Environment["FGO_PET_STATE_ROOT"] = _options.StateRoot;
        info.Environment["FGO_PET_PIPE_SUFFIX"] = _options.PipeSuffix;

        using var process = Process.Start(info) ?? throw new IOException("adapter_start_failed");
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderr = DrainAsync(process.StandardError.BaseStream, cancellationToken);
        var exit = process.WaitForExitAsync(cancellationToken);
        try
        {
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                await completed.ConfigureAwait(false);
            }

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
            }
        }
    }

    private string GetAdapterPath() =>
        Path.Combine(Path.GetDirectoryName(_options.RelayExecutablePath)!, "FgoPet.CodexAdapter.exe");

    private static string ValidateText(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > MaxTextLength
            || normalized.Any(char.IsControl) || AgentPayloadSanitizer.ContainsForbiddenText(normalized))
        {
            throw InvalidResponse();
        }

        return normalized;
    }

    private static InvalidDataException InvalidResponse(Exception? inner = null) =>
        new("adapter_invalid_response", inner);

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        while (true)
        {
            var remaining = MaxStdoutBytes - output.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            }

            if (read > remaining)
            {
                throw InvalidResponse();
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    private sealed record AdapterTarget(string? TargetId, string? DisplayName, string? Directory, bool? ReadOnly);
}
