using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FgoPet.Infrastructure.Secrets;

/// <summary>Windows Credential Manager adapter. It never exposes a read-secret API.</summary>
public sealed class WindowsCredentialStore : ICredentialStore, ICredentialReader
{
    private const uint GenericCredentialType = 1;
    private const uint ErrorNotFound = 1168;
    private const uint PersistLocalMachine = 2;

    public Task SaveAsync(string target, string secret, CancellationToken cancellationToken)
    {
        Validate(target, secret);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var secretPtr = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPtr, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetPtr,
                CredentialBlob = secretPtr,
                CredentialBlobSize = (uint)secretBytes.Length,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential Manager could not save the credential.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(secretPtr);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!CredRead(target, GenericCredentialType, 0, out var credentialPtr))
        {
            var error = (uint)Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult(false);
            }

            throw new Win32Exception((int)error, "Credential Manager could not check the credential.");
        }

        try
        {
            return Task.FromResult(true);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!CredRead(target, GenericCredentialType, 0, out var credentialPtr))
        {
            var error = (uint)Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception((int)error, "Credential Manager could not read the credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(bytes));
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public Task DeleteAsync(string target, CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!CredDelete(target, GenericCredentialType, 0))
        {
            var error = (uint)Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception((int)error, "Credential Manager could not delete the credential.");
            }
        }

        return Task.CompletedTask;
    }

    private static void Validate(string target, string secret)
    {
        ValidateTarget(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret, nameof(secret));
    }

    private static void ValidateTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(target));
        if (target.Length > 512)
        {
            throw new ArgumentException("Credential target is too long.", nameof(target));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree([In] IntPtr credential);
}
