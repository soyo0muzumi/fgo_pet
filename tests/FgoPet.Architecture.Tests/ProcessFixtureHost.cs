using System.Diagnostics;
using System.Globalization;

namespace FgoPet.Architecture.Tests;

/// <summary>
/// Test-only entry point for a genuine parent/child process tree. VSTest still
/// discovers tests in this assembly; only explicit fixture arguments run this host.
/// It avoids measuring PowerShell startup as part of the evaluator's cancellation test.
/// </summary>
internal static class ProcessFixtureHost
{
    private const string ParentMode = "--architecture-process-parent";
    private const string ChildMode = "--architecture-process-child";

    public static int Main(string[] args)
    {
        if (args is [ChildMode])
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (args is not [ParentMode, var pidFile]) return 2;

        Process? child = null;
        try
        {
            child = Process.Start(CreateStartInfo(ChildMode))
                ?? throw new InvalidOperationException("process-fixture: child did not start");
            File.WriteAllText(pidFile + ".tmp", child.Id.ToString(CultureInfo.InvariantCulture));
            File.Move(pidFile + ".tmp", pidFile);
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("process-fixture: host failed before completion");
            return 3;
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                finally { child.Dispose(); }
            }
        }
    }

    internal static ProcessStartInfo CreateParentStartInfo(string pidFile) => CreateStartInfo(ParentMode, pidFile);

    private static ProcessStartInfo CreateStartInfo(string mode, string? pidFile = null)
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo
        {
            FileName = !string.IsNullOrWhiteSpace(configuredHost) && File.Exists(configuredHost) ? configuredHost : "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(typeof(ProcessFixtureHost).Assembly.Location);
        info.ArgumentList.Add(mode);
        if (pidFile is not null) info.ArgumentList.Add(pidFile);
        return info;
    }
}
