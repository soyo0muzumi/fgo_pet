using FgoPet.AgentRuntime;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class CodexTargetCatalogClientTests
{
    [Fact]
    public void Parse_projects_adapter_targets_without_exposing_directory()
    {
        var json = "[{\"TargetId\":\"target-1\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\\\\project\",\"ReadOnly\":true}]";

        var result = CodexTargetCatalogClient.Parse(json);

        var target = Assert.Single(result);
        Assert.Equal("target-1", target.TargetId);
        Assert.Equal("Project", target.DisplayName);
        Assert.True(target.IsReadOnly);
        Assert.DoesNotContain("Directory", target.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\\\work", target.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[{\"TargetId\":\"C:\\\\path\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\",\"ReadOnly\":false}]")]
    [InlineData("[{\"TargetId\":\"target-1\",\"DisplayName\":\"bad\\nname\",\"Directory\":\"C:\\\\work\",\"ReadOnly\":false}]")]
    public void Parse_rejects_unsafe_catalog_entries(string json)
    {
        Assert.Throws<InvalidDataException>(() => CodexTargetCatalogClient.Parse(json));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Parse_rejects_malformed_or_empty_catalogs(string json)
    {
        Assert.Throws<InvalidDataException>(() => CodexTargetCatalogClient.Parse(json));
    }

    [Fact]
    public void Parse_rejects_unpaired_surrogates_as_invalid_response()
    {
        Assert.Throws<InvalidDataException>(() => CodexTargetCatalogClient.Parse("\uD800"));
    }

    [Fact]
    public async Task ListAsync_maps_nonzero_adapter_exit_to_safe_unavailable_status()
    {
        using var environment = TestEnvironment.CreateInstalled();
        var client = new CodexTargetCatalogClient(
            environment.Options,
            _ => Task.FromResult((17, "stderr: C:\\\\private\\\\error.log")));

        var result = await client.ListAsync();

        Assert.Equal(AgentTargetCatalogStatus.AdapterUnavailable, result.Status);
        Assert.Empty(result.Targets);
        Assert.Equal("adapter_unavailable", result.SafeError);
        Assert.DoesNotContain("stderr", result.SafeError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\\\private", result.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_maps_internal_runner_cancellation_to_safe_timeout_status()
    {
        using var environment = TestEnvironment.CreateInstalled();
        var client = new CodexTargetCatalogClient(
            environment.Options,
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (0, "[]");
            });

        var result = await client.ListAsync();

        Assert.Equal(AgentTargetCatalogStatus.TimedOut, result.Status);
        Assert.Empty(result.Targets);
        Assert.Equal("adapter_timeout", result.SafeError);
    }

    [Fact]
    public async Task ListAsync_rejects_oversized_stdout_without_returning_payload()
    {
        using var environment = TestEnvironment.CreateInstalled();
        var oversized = new string('x', 1024 * 1024 + 1);
        var client = new CodexTargetCatalogClient(
            environment.Options,
            _ => Task.FromResult((0, oversized)));

        var result = await client.ListAsync();

        Assert.Equal(AgentTargetCatalogStatus.InvalidResponse, result.Status);
        Assert.Empty(result.Targets);
        Assert.Equal("adapter_invalid_response", result.SafeError);
        Assert.DoesNotContain("x", result.SafeError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[{\"TargetId\":\"target-1\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\"}]")]
    [InlineData("[{\"TargetId\":\"target-1\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\",\"ReadOnly\":false},{\"TargetId\":\"target-1\",\"DisplayName\":\"Other\",\"Directory\":\"C:\\\\other\",\"ReadOnly\":false}]")]
    public async Task ListAsync_maps_invalid_json_catalogs_to_safe_invalid_response(string stdout)
    {
        using var environment = TestEnvironment.CreateInstalled();
        var client = new CodexTargetCatalogClient(
            environment.Options,
            _ => Task.FromResult((0, stdout)));

        var result = await client.ListAsync();

        Assert.Equal(AgentTargetCatalogStatus.InvalidResponse, result.Status);
        Assert.Empty(result.Targets);
        Assert.Equal("adapter_invalid_response", result.SafeError);
        Assert.DoesNotContain("target-1", result.SafeError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\\\", result.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_maps_missing_adapter_to_not_installed_without_starting_runner()
    {
        using var environment = TestEnvironment.CreateMissingAdapter();
        var started = false;
        var client = new CodexTargetCatalogClient(
            environment.Options,
            _ =>
            {
                started = true;
                return Task.FromResult((0, "[]"));
            });

        var result = await client.ListAsync();

        Assert.Equal(AgentTargetCatalogStatus.AdapterNotInstalled, result.Status);
        Assert.Empty(result.Targets);
        Assert.Equal("adapter_not_installed", result.SafeError);
        Assert.False(started);
    }

    [Fact]
    public async Task ListAsync_preserves_explicit_caller_cancellation()
    {
        using var environment = TestEnvironment.CreateInstalled();
        using var cancellation = new CancellationTokenSource();
        var client = new CodexTargetCatalogClient(
            environment.Options,
            async cancellationToken =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (0, "[]");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListAsync(cancellation.Token));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root, RelayRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }
        public RelayRuntimeOptions Options { get; }

        public static TestEnvironment CreateInstalled()
        {
            var root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "FgoPet.AgentRelay.exe"), string.Empty);
            File.WriteAllText(Path.Combine(root, "FgoPet.CodexAdapter.exe"), string.Empty);
            return Create(root);
        }

        public static TestEnvironment CreateMissingAdapter()
        {
            var root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "FgoPet.AgentRelay.exe"), string.Empty);
            return Create(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static TestEnvironment Create(string root) => new(
            root,
            new RelayRuntimeOptions(
                "test",
                Path.Combine(root, "state"),
                Path.Combine(root, "FgoPet.AgentRelay.exe"),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1)));

        private static string CreateRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "fgo-pet-target-client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
