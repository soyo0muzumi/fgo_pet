using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FgoPet.Tests.Diagnostics;

// Opt-in metadata for synthetic fixtures only. Never reads file contents or changes permissions.
internal static class SyntheticFileOperationDiagnostics
{
    internal static void Record(string variable, string phase, string result, string source, string destination, Exception? error = null)
    {
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return;
        try
        {
            var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..16];
            File.WriteAllText(Path.Combine(directory, correlation + "-" + result + ".json"), JsonSerializer.Serialize(new
            {
                atUtc = DateTimeOffset.UtcNow,
                phase,
                result,
                correlation,
                source = Inspect(source),
                destination = Inspect(destination),
                parent = Inspect(Path.GetDirectoryName(destination)!),
                errorType = error?.GetType().FullName,
                hResult = error?.HResult,
                nativeErrorCode = error is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null,
                exception = error?.ToString(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    private static object Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var attributes = File.GetAttributes(fullPath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            string? accessControl = null;
            string? accessControlError = null;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
                    FileSystemSecurity security = isDirectory
                        ? new DirectoryInfo(fullPath).GetAccessControl(sections)
                        : new FileInfo(fullPath).GetAccessControl(sections);
                    accessControl = security.GetSecurityDescriptorSddlForm(sections);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    accessControlError = failure.GetType().Name;
                }
            }
            return new
            {
                path = fullPath,
                exists = true,
                attributes = attributes.ToString(),
                bytes = isDirectory ? (long?)null : new FileInfo(fullPath).Length,
                accessControl,
                accessControlError,
            };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new
            {
                path = fullPath,
                exists = failure is FileNotFoundException or DirectoryNotFoundException ? (bool?)false : null,
                errorCode = failure.GetType().Name,
                failure.HResult,
            };
        }
    }
}
