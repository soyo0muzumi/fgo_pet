using System.Diagnostics;
using System.Text;
using Xunit;

namespace FgoPet.Architecture.Tests;

public sealed class ProjectReferenceEvaluatorTests
{
    [Fact]
    public async Task Evaluates_project_references_in_declared_order_and_classifies_companion()
    {
        using var fixture = TemporaryProject.Create();
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator();

        var result = await evaluator.EvaluateAsync(fixture.ProjectPath, "Debug");

        Assert.Equal(Path.GetFullPath(fixture.ProjectPath), result.ProjectPath);
        Assert.Equal("net8.0", result.TargetFramework);
        Assert.Equal(new[] { "win-x64", "linux-x64" }, result.RuntimeIdentifiers);
        Assert.Collection(
            result.References,
            reference =>
            {
                Assert.Equal(Path.GetFullPath(fixture.NormalReferencePath), reference.ProjectPath);
                Assert.Equal(ProjectReferenceKind.Compile, reference.Kind);
            },
            reference =>
            {
                Assert.Equal(Path.GetFullPath(fixture.CompanionReferencePath), reference.ProjectPath);
                Assert.Equal(ProjectReferenceKind.Companion, reference.Kind);
            });
    }

    [Fact]
    public async Task Evaluates_app_release_win_x64_and_finds_both_companions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "FgoPet.App", "FgoPet.App.csproj");
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator();

        var result = await evaluator.EvaluateAsync(projectPath, "Release", "win-x64");

        Assert.Equal(Path.GetFullPath(projectPath), result.ProjectPath);
        Assert.Equal("net8.0-windows", result.TargetFramework);
        Assert.Contains("win-x64", result.RuntimeIdentifiers);
        Assert.Collection(
            result.References.Where(reference => reference.Kind == ProjectReferenceKind.Companion),
            reference => Assert.EndsWith(Path.Combine("FgoPet.AgentRelay", "FgoPet.AgentRelay.csproj"), reference.ProjectPath),
            reference => Assert.EndsWith(Path.Combine("FgoPet.CodexAdapter", "FgoPet.CodexAdapter.csproj"), reference.ProjectPath));
    }

    [Fact]
    public async Task Missing_project_throws_safe_error_without_tool_output()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csproj");
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(missingPath, "Release"));

        Assert.Contains(Path.GetFullPath(missingPath), error.Message);
        Assert.DoesNotContain("The project file could not be found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nonzero_process_result_does_not_embed_tool_output()
    {
        var factory = new FakeProcessFactory(exitCode: 17, standardError: "private tool output", standardOutput: "private stdout");
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator(factory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync("project.csproj", "Release"));

        Assert.Contains("project.csproj", error.Message);
        Assert.Contains("17", error.Message);
        Assert.DoesNotContain("private tool output", error.Message);
        Assert.DoesNotContain("private stdout", error.Message);
    }

    [Fact]
    public async Task Cancellation_kills_running_process_tree()
    {
        var factory = new FakeProcessFactory(waitForCancellation: true);
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator(factory);
        using var cancellation = new CancellationTokenSource();

        var evaluation = evaluator.EvaluateAsync("project.csproj", "Release", "win-x64", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.True(factory.Process.KillCalledWithEntireProcessTree);
        var arguments = string.Join(" ", factory.StartInfo.ArgumentList);
        Assert.DoesNotContain("restore", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-t:Build", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-target:Build", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-getProperty:TargetFramework,RuntimeIdentifiers", arguments);
        Assert.Contains("-getItem:ProjectReference", arguments);
        Assert.Contains("-p:Configuration=Release", arguments);
        Assert.Contains("-p:RuntimeIdentifier=win-x64", arguments);
    }

    [Fact]
    public async Task Cancellation_waits_for_process_cleanup_before_propagating()
    {
        var factory = new CleanupTrackingProcessFactory();
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator(factory);
        using var cancellation = new CancellationTokenSource();
        var evaluation = evaluator.EvaluateAsync("project.csproj", "Release", cancellationToken: cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);

        Assert.True(factory.Process.KillCalledWithEntireProcessTree);
        Assert.True(factory.Process.CleanupWaitCalled);
        Assert.True(factory.Process.StandardOutputWasObserved);
        Assert.True(factory.Process.StandardErrorWasObserved);
    }

    [Fact]
    public async Task Cancellation_terminates_real_process_tree_and_completes_waits()
    {
        if (!OperatingSystem.IsWindows()) return;
        await ExerciseRealProcessAsync(cancelReadiness: false);
    }

    [Fact]
    public async Task Cancelled_readiness_wait_still_terminates_real_process_tree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExerciseRealProcessAsync(cancelReadiness: true));
        Assert.Equal(new CancellationToken(canceled: true), error.CancellationToken);
    }

    [Fact]
    public async Task Exited_parent_is_reported_before_the_readiness_deadline()
    {
        using var fixture = RealProcessFixture.Create();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WaitForChildPidAsync(Task.CompletedTask));
        Assert.Equal("process-fixture: parent exited before child readiness", error.Message);
    }

    private static async Task ExerciseRealProcessAsync(bool cancelReadiness)
    {
        using var fixture = RealProcessFixture.Create();
        var factory = new RealChildProcessFactory(fixture.ChildPidFile);
        var evaluator = new DotNetMsBuildProjectReferenceEvaluator(factory);
        using var cancellation = new CancellationTokenSource();
        Task? evaluation = null;
        int? childPid = null;

        // Startup and readiness must be inside the same cleanup scope as cancellation.
        try
        {
            evaluation = evaluator.EvaluateAsync("project.csproj", "Release", "win-x64", cancellation.Token);
            childPid = await fixture.WaitForChildPidAsync(evaluation);
            Assert.True(IsProcessAlive(childPid.Value));
            if (cancelReadiness)
            {
                // Inject a failed readiness wait with a known live child so the failure
                // path must prove that both processes and all evaluator waits are cleaned up.
                await fixture.WaitForChildPidAsync(evaluation, new CancellationToken(canceled: true));
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);

            // Prove the evaluator itself terminated both processes before fixture fallback cleanup.
            await WaitUntilAsync(() => !IsProcessAlive(factory.ProcessId) && !IsProcessAlive(childPid.Value));
            Assert.All(factory.Handle.WaitTasks, wait => Assert.True(wait.IsCompleted));
        }
        finally
        {
            cancellation.Cancel();
            try { await factory.KillTreeIfRunningAsync(); }
            finally
            {
                if (childPid is int pid) TryKill(pid);
                if (evaluation is not null)
                {
                    try { await evaluation.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            }
            if (childPid is int knownChild)
            {
                await WaitUntilAsync(() => !IsProcessAlive(factory.ProcessId) && !IsProcessAlive(knownChild));
                Assert.All(factory.Handle.WaitTasks, wait => Assert.True(wait.IsCompleted));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FgoPet.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // A cleanup timeout is a failure, never the expected caller-cancellation result.
            throw new TimeoutException("process-fixture: process tree exit deadline exceeded");
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
            // It may have exited between HasExited and Kill.
        }
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string root)
        {
            Root = root;
            NormalReferencePath = Path.Combine(root, "Normal", "Normal.csproj");
            CompanionReferencePath = Path.Combine(root, "Companion", "Companion.csproj");
            ProjectPath = Path.Combine(root, "Host.csproj");
        }

        public string Root { get; }
        public string ProjectPath { get; }
        public string NormalReferencePath { get; }
        public string CompanionReferencePath { get; }

        public static TemporaryProject Create()
        {
            var fixture = new TemporaryProject(Path.Combine(Path.GetTempPath(), $"fgo-architecture-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.NormalReferencePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.CompanionReferencePath)!);
            File.WriteAllText(fixture.NormalReferencePath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(fixture.CompanionReferencePath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(fixture.ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <RuntimeIdentifiers>win-x64;linux-x64;;</RuntimeIdentifiers>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Normal/Normal.csproj" />
                    <ProjectReference Include="Companion/Companion.csproj" ReferenceOutputAssembly="False" />
                  </ItemGroup>
                </Project>
                """);
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeProcessFactory : IProcessFactory
    {
        public FakeProcessFactory(int exitCode = 0, string standardError = "", string standardOutput = "", bool waitForCancellation = false)
        {
            Process = new FakeProcess(exitCode, standardError, standardOutput, waitForCancellation);
        }

        public FakeProcess Process { get; }
        public ProcessStartInfo StartInfo { get; private set; } = null!;

        public IProcessHandle Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return Process;
        }
    }

    private sealed class CleanupTrackingProcessFactory : IProcessFactory
    {
        public CleanupTrackingProcess Process { get; } = new();

        public IProcessHandle Start(ProcessStartInfo startInfo) => Process;
    }

    private sealed class CleanupTrackingProcess : IProcessHandle
    {
        private readonly StreamReader _standardOutput = new(new MemoryStream(Encoding.UTF8.GetBytes("output")));
        private readonly StreamReader _standardError = new(new MemoryStream(Encoding.UTF8.GetBytes("error")));

        public bool HasExited { get; private set; }
        public bool KillCalledWithEntireProcessTree { get; private set; }
        public bool CleanupWaitCalled { get; private set; }
        public bool StandardOutputWasObserved { get; private set; }
        public bool StandardErrorWasObserved { get; private set; }
        public StreamReader StandardOutput
        {
            get
            {
                StandardOutputWasObserved = true;
                return _standardOutput;
            }
        }
        public StreamReader StandardError
        {
            get
            {
                StandardErrorWasObserved = true;
                return _standardError;
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            CleanupWaitCalled = true;
            HasExited = true;
            return 0;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalledWithEntireProcessTree = entireProcessTree;
        }

        public void Dispose()
        {
            _standardOutput.Dispose();
            _standardError.Dispose();
        }
    }

    private sealed class RealProcessFixture : IDisposable
    {
        private RealProcessFixture(string root)
        {
            Root = root;
            ChildPidFile = Path.Combine(root, "child.pid");
        }

        public string Root { get; }
        public string ChildPidFile { get; }

        public static RealProcessFixture Create()
        {
            var fixture = new RealProcessFixture(Path.Combine(Path.GetTempPath(), $"fgo-architecture-process-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(fixture.Root);
            return fixture;
        }

        public async Task<int> WaitForChildPidAsync(Task evaluation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(ChildPidFile))
                    {
                        var text = await File.ReadAllTextAsync(ChildPidFile, timeout.Token);
                        if (int.TryParse(text, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0) return pid;
                        throw new InvalidOperationException("process-fixture: invalid child readiness record");
                    }
                    if (evaluation.IsCompleted)
                    {
                        await evaluation;
                        throw new InvalidOperationException("process-fixture: parent exited before child readiness");
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("process-fixture: child readiness deadline exceeded");
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RealChildProcessFactory : IProcessFactory
    {
        private readonly string _childPidFile;

        public RealChildProcessFactory(string childPidFile) => _childPidFile = childPidFile;

        public TrackingProcessHandle Handle { get; private set; } = null!;
        public int ProcessId => Handle.ProcessId;

        public IProcessHandle Start(ProcessStartInfo _)
        {
            // Unlike cmd.exe's interactive timeout command, this child cannot exit merely
            // because stdin is redirected. Its lifetime ends only when the process tree is killed.
            var childCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "[System.Threading.Thread]::Sleep([System.Threading.Timeout]::Infinite)"));
            var pidFile = _childPidFile.Replace("'", "''");
            var script = $"$ErrorActionPreference='Stop'; $child=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','{childCommand}' -PassThru; [IO.File]::WriteAllText('{pidFile}.tmp', [string]$child.Id); [IO.File]::Move('{pidFile}.tmp', '{pidFile}'); Wait-Process -Id $child.Id";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start process fixture.");
            Handle = new TrackingProcessHandle(new ProcessHandle(process), process.Id);
            return Handle;
        }

        public async Task KillTreeIfRunningAsync()
        {
            try
            {
                if (Handle is not null && !Handle.HasExited)
                {
                    Handle.Kill(entireProcessTree: true);
                    await Handle.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (InvalidOperationException)
            {
                // The evaluator disposes the handle after its task completes.
            }
        }
    }

    private sealed class TrackingProcessHandle : IProcessHandle
    {
        private readonly IProcessHandle _inner;

        public TrackingProcessHandle(IProcessHandle inner, int processId)
        {
            _inner = inner;
            ProcessId = processId;
        }

        public int ProcessId { get; }
        public System.Collections.Concurrent.ConcurrentBag<Task<int>> WaitTasks { get; } = new();
        public StreamReader StandardOutput => _inner.StandardOutput;
        public StreamReader StandardError => _inner.StandardError;
        public bool HasExited => _inner.HasExited;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            var task = _inner.WaitForExitAsync(cancellationToken);
            WaitTasks.Add(task);
            return task;
        }
        public void Kill(bool entireProcessTree) => _inner.Kill(entireProcessTree);
        public void Dispose() => _inner.Dispose();
    }

    private sealed class FakeProcess : IProcessHandle
    {
        private readonly int _exitCode;
        private readonly bool _waitForCancellation;

        public FakeProcess(int exitCode, string standardError, string standardOutput, bool waitForCancellation)
        {
            _exitCode = exitCode;
            _waitForCancellation = waitForCancellation;
            StandardError = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(standardError)));
            StandardOutput = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(standardOutput)));
        }

        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }
        public bool HasExited { get; private set; }
        public bool KillCalledWithEntireProcessTree { get; private set; }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (_waitForCancellation && !HasExited)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            HasExited = true;
            return _exitCode;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalledWithEntireProcessTree = entireProcessTree;
            HasExited = true;
        }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
