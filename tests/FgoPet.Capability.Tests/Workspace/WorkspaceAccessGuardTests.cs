using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Workspace;

public sealed class WorkspaceAccessGuardTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("\\\\?\\C:\\file.txt")]
    [InlineData("C:relative.txt")]
    public void Untrusted_path_forms_are_denied(string path)
    {
        using var f = new Fixture();
        Assert.Throws<WorkspaceAccessException>(() => f.Guard.Open(f.Scope, path, WorkspacePathKind.File, allowCreate: true));
    }

    [Fact]
    public void Prefix_collision_and_unbound_scope_are_denied()
    {
        using var f = new Fixture();
        Assert.Throws<WorkspaceAccessException>(() => f.Guard.Open(f.Scope, f.Root + "-other\\fixture.txt", WorkspacePathKind.File, true));
        Assert.Throws<WorkspaceAccessException>(() => f.Guard.Open(new("other", "role", null), "fixture.txt", WorkspacePathKind.File));
    }

    [Fact]
    public void Lease_checks_authority_revision_and_target_identity_again()
    {
        using var f = new Fixture();
        using var lease = f.Guard.Open(f.Scope, "fixture.txt", WorkspacePathKind.File);
        Assert.Equal(Path.Combine(f.Root, "fixture.txt"), lease.FullPath);
        lease.Revalidate();
        f.Current = f.Current with { Authorization = new("root", 2) };
        Assert.Throws<WorkspaceAccessException>(lease.Revalidate);
    }

    [Fact]
    public void Same_name_replaced_file_is_detected()
    {
        using var f = new Fixture();
        using var lease = f.Guard.Open(f.Scope, "fixture.txt", WorkspacePathKind.File);
        File.Move(lease.FullPath, Path.Combine(f.Root, "old.txt"));
        File.WriteAllText(lease.FullPath, "replacement");
        Assert.Throws<WorkspaceAccessException>(lease.Revalidate);
    }

    [Fact]
    public void Resource_binding_changes_when_root_is_replaced_at_same_name()
    {
        using var f = new Fixture();
        var before = f.Guard.GetAuthorization(f.Scope).Authorization;
        var moved = f.Root + "-old";
        Directory.Move(f.Root, moved);
        Directory.CreateDirectory(f.Root);
        try { Assert.NotEqual(before, f.Guard.GetAuthorization(f.Scope).Authorization); }
        finally { File.Delete(Path.Combine(moved, "fixture.txt")); Directory.Delete(moved); }
    }

    [Fact]
    public void Reparse_point_and_new_file_appearing_after_authorization_are_denied()
    {
        using var f = new Fixture();
        var link = Path.Combine(f.Root, "linked");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, f.Root }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Throws<WorkspaceAccessException>(() => f.Guard.Open(f.Scope, "linked/fixture.txt", WorkspacePathKind.File));
        }
        finally { Directory.Delete(link); }
        using var lease = f.Guard.Open(f.Scope, "new.txt", WorkspacePathKind.File, true);
        File.WriteAllText(lease.FullPath, "appeared");
        Assert.Throws<WorkspaceAccessException>(lease.Revalidate);
    }

    internal sealed class Fixture : IWorkspaceAuthorizationSource, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fgo-workspace-" + Guid.NewGuid().ToString("N"));
        public ToolScope Scope { get; } = new("conversation", "role", "project");
        public WorkspaceAuthorization Current { get; set; }
        public WorkspaceAccessGuard Guard { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "fixture.txt"), "fixture");
            Current = new(Scope, new("root", 1), Root, Root);
            Guard = new(this);
        }
        public WorkspaceAuthorization? GetCurrent(ToolScope scope) => scope == Scope ? Current : null;
        public void Dispose() { foreach (var path in Directory.GetFiles(Root)) File.Delete(path); Directory.Delete(Root); }
    }
}
