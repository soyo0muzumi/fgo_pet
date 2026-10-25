using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Extensibility;
using Microsoft.Win32.SafeHandles;

namespace FgoPet.Plugin.Workspace;

/// <summary>Windows file access boundary. Parents are pinned; targets are identity-checked again before use.</summary>
public sealed class WorkspaceAccessGuard(IWorkspaceAuthorizationSource source) : IWorkspaceAccessGuard
{
    public WorkspaceAuthorization GetAuthorization(ToolScope scope)
    {
        var authority = source.GetCurrent(scope);
        if (authority is null || authority.Scope != scope || authority.Authorization is null ||
            string.IsNullOrWhiteSpace(authority.Authorization.Id) || authority.Authorization.Id.Length > 128 ||
            authority.Authorization.Revision < 0) throw Denied("WORKSPACE_UNAVAILABLE");
        var root = Canonical(authority.RootPath, null);
        var current = Canonical(authority.CurrentDirectory, null);
        if (!Within(root, current) || !Directory.Exists(root) || !Directory.Exists(current)) throw Denied("WORKSPACE_SCOPE_DENIED");
        using var rootHandle = OpenIdentity(root, false, out var rootAttributes);
        using var currentHandle = OpenIdentity(current, false, out var currentAttributes);
        if ((rootAttributes & currentAttributes & FileAttributes.Directory) == 0) throw Denied("WORKSPACE_SCOPE_DENIED");
        var rootIdentity = Identity(rootHandle);
        var currentIdentity = Identity(currentHandle);
        return authority with { RootPath = root, CurrentDirectory = current, Authorization = authority.Authorization with {
            Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { authority.Scope, Root = root.ToUpperInvariant(), Current = current.ToUpperInvariant(), rootIdentity, currentIdentity }))) } };
    }

    public IWorkspacePathLease Open(ToolScope scope, string path, WorkspacePathKind kind, bool allowCreate = false)
    {
        if (!Enum.IsDefined(kind) || kind == WorkspacePathKind.Directory && allowCreate) throw Denied("WORKSPACE_PATH_DENIED");
        var authority = GetAuthorization(scope);
        var full = Canonical(path, authority.CurrentDirectory);
        if (!Within(authority.RootPath, full)) throw Denied("WORKSPACE_PATH_DENIED");
        var parents = new List<SafeFileHandle>();
        try
        {
            var parent = kind == WorkspacePathKind.Directory ? full : Path.GetDirectoryName(full)!;
            foreach (var ancestor in Ancestors(parent))
            {
                var handle = OpenIdentity(ancestor, pinDirectory: true, out var attributes);
                parents.Add(handle);
                if ((attributes & FileAttributes.Directory) == 0) throw Denied("WORKSPACE_PATH_DENIED");
            }
            var exists = File.Exists(full) || Directory.Exists(full);
            FileIdentity? target = null;
            if (exists)
            {
                using var handle = OpenIdentity(full, pinDirectory: false, out var attributes);
                var directory = (attributes & FileAttributes.Directory) != 0;
                if (directory != (kind == WorkspacePathKind.Directory)) throw Denied("WORKSPACE_PATH_DENIED");
                target = Identity(handle);
            }
            else if (!allowCreate) throw Denied("WORKSPACE_NOT_FOUND");
            var lease = new Lease(this, authority, full, exists, target, parents);
            lease.Revalidate();
            return lease;
        }
        catch (WorkspaceAccessException) { foreach (var handle in parents) handle.Dispose(); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { foreach (var handle in parents) handle.Dispose(); throw Denied("WORKSPACE_PATH_DENIED"); }
    }

    private sealed class Lease(WorkspaceAccessGuard owner, WorkspaceAuthorization authority, string path,
        bool exists, FileIdentity? identity, List<SafeFileHandle> parents) : IWorkspacePathLease
    {
        private bool _disposed;
        public WorkspaceAuthorization Authority => authority;
        public string FullPath => path;
        public bool Exists => exists;
        public void ValidateOpenedFile(FileStream file)
        {
            Revalidate();
            if (!exists || identity is null || Identity(file.SafeFileHandle) != identity) throw Denied("SOURCE_CHANGED");
            if (!GetFileInformationByHandle(file.SafeFileHandle, out var info) ||
                ((FileAttributes)info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw Denied("WORKSPACE_PATH_DENIED");
        }
        public void Revalidate()
        {
            if (_disposed || owner.GetAuthorization(authority.Scope) != authority) throw Denied("WORKSPACE_SCOPE_CHANGED");
            try
            {
                foreach (var parent in parents)
                {
                    if (parent.IsClosed || parent.IsInvalid || !GetFileInformationByHandle(parent, out var info) ||
                        ((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0) throw Denied("WORKSPACE_PATH_CHANGED");
                }
                var nowExists = File.Exists(path) || Directory.Exists(path);
                if (nowExists != exists) throw Denied("SOURCE_CHANGED");
                if (exists)
                {
                    using var current = OpenIdentity(path, pinDirectory: false, out _);
                    if (Identity(current) != identity) throw Denied("SOURCE_CHANGED");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Denied("WORKSPACE_PATH_CHANGED"); }
        }
        public void Dispose() { if (_disposed) return; _disposed = true; foreach (var handle in parents) handle.Dispose(); }
    }

    private static string Canonical(string? path, string? current)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) ||
            path.StartsWith('\\') || path.StartsWith('/') || path.Contains('"') || path.Contains('|') ||
            path.Contains('*') || path.Contains('?') || path.Contains('<') || path.Contains('>')) throw Denied("WORKSPACE_PATH_DENIED");
        path = path.Replace('/', '\\');
        var rooted = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        if (path.Contains(':') && (!rooted || path.AsSpan(2).Contains(':')) || current is null && !rooted)
            throw Denied("WORKSPACE_PATH_DENIED");
        var segments = (rooted ? path[3..] : path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == ".." || segment != "." && segment.TrimEnd(' ', '.') != segment || Reserved(segment))
                throw Denied("WORKSPACE_PATH_DENIED");
        }
        try { return Path.TrimEndingDirectorySeparator(rooted ? Path.GetFullPath(path) : Path.GetFullPath(path, current!)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Denied("WORKSPACE_PATH_DENIED"); }
    }

    private static bool Reserved(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && stem[3] is >= '1' and <= '9' && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal));
    }
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static IEnumerable<string> Ancestors(string parent)
    {
        var stack = new Stack<string>();
        for (string? path = parent; path is not null; path = Path.GetDirectoryName(path)) stack.Push(path);
        return stack;
    }
    private static SafeFileHandle OpenIdentity(string path, bool pinDirectory, out FileAttributes attributes)
    {
        var handle = CreateFileW(path, 0, pinDirectory ? 3u : 7u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Denied("WORKSPACE_PATH_DENIED"); }
        if (!GetFileInformationByHandle(handle, out var info) || ((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0)
        { handle.Dispose(); throw Denied("WORKSPACE_REPARSE_DENIED"); }
        attributes = (FileAttributes)info.Attributes;
        return handle;
    }
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Denied("WORKSPACE_PATH_DENIED");
        return new(info.VolumeSerial, info.IndexHigh, info.IndexLow);
    }
    private readonly record struct FileIdentity(uint Volume, uint High, uint Low);
    private static WorkspaceAccessException Denied(string code) => new(code);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
