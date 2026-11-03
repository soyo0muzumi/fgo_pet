<#
.SYNOPSIS
    Stores a platform session for the Social Sources plugin and enables its session grant.

.DESCRIPTION
    There is no settings page for the authenticated social sources yet, so this script performs the two
    host-side steps the plugin expects:

      1. write the session cookie into Windows Credential Manager as a generic credential under
         FgoPet.SocialSources.<platform>, using the same Win32 call the application itself reads
      2. write social-session-<platform>.json in the app state root, with a fresh authorization id and a
         positive revision

    The cookie is never written to a file by this script; only the grant (which contains no secret) is.
    Use -Clear to revoke: it removes the stored credential and disables the grant.

    For 小红书 the cookie is stored but the grant is written disabled: this build cannot sign the
    platform's search request yet, so enabling it would only send requests that are certain to be refused.

.PARAMETER Platform
    bilibili, xiaohongshu or xiaoheihe.

.PARAMETER Cookie
    The full Cookie request header value copied from the signed-in browser tab. Pasting the whole header
    is intended: the plugin adds its own anonymous device cookie where a platform needs one, and extra
    cookies are harmless.

.PARAMETER Clear
    Revoke instead of configure.

.PARAMETER StateRoot
    Overrides the app state root. Defaults to FGO_PET_STATE_ROOT, else %LOCALAPPDATA%\FgoPet.

.EXAMPLE
    .\Set-SocialSession.ps1 -Platform xiaohongshu -Cookie 'web_session=...; a1=...'

.EXAMPLE
    .\Set-SocialSession.ps1 -Platform bilibili -Clear
#>
[CmdletBinding(DefaultParameterSetName = 'Configure')]
param(
    [Parameter(Mandatory)]
    [ValidateSet('bilibili', 'xiaohongshu', 'xiaoheihe')]
    [string] $Platform,

    [Parameter(Mandatory, ParameterSetName = 'Configure')]
    [string] $Cookie,

    [Parameter(Mandatory, ParameterSetName = 'Clear')]
    [switch] $Clear,

    [Parameter(ParameterSetName = 'Configure')]
    [Parameter(ParameterSetName = 'Clear')]
    [string] $StateRoot
)

$ErrorActionPreference = 'Stop'

# Minimal Credential Manager interop. Deliberately identical to the app's WindowsCredentialStore:
# CRED_TYPE_GENERIC (1), CRED_PERSIST_LOCAL_MACHINE (2), UTF-16 blob.
if (-not ('SocialSessionCredential' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class SocialSessionCredential
{
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    public static void Save(string target, string userName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = GenericCredentialType,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = PersistLocalMachine,
                UserName = userName,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential Manager could not save the session.");
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static void Delete(string target) => CredDelete(target, GenericCredentialType, 0);
}
'@
}

if (-not $StateRoot) {
    $StateRoot = if ($env:FGO_PET_STATE_ROOT) {
        $env:FGO_PET_STATE_ROOT
    } else {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'FgoPet'
    }
}
if (-not [IO.Path]::IsPathFullyQualified($StateRoot)) {
    throw "The state root must be an absolute path: $StateRoot"
}
$StateRoot = [IO.Path]::GetFullPath($StateRoot)

$credentialTarget = "FgoPet.SocialSources.$Platform"
$grantPath = Join-Path $StateRoot "social-session-$Platform.json"

function Read-Revision {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 1 }
    try {
        $previous = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($previous.AuthorizationVersion -and [long]$previous.AuthorizationVersion -gt 0) {
            return [long]$previous.AuthorizationVersion + 1
        }
    } catch { }
    return 1
}

if ($Clear) {
    # Remove the secret first: a leftover grant without a secret is a safe state, the reverse is not.
    [SocialSessionCredential]::Delete($credentialTarget)
    if (Test-Path -LiteralPath $grantPath) {
        $grant = [ordered]@{
            Enabled              = $false
            AuthorizationId      = [Guid]::NewGuid().ToString('N')
            AuthorizationVersion = (Read-Revision -Path $grantPath)
            Platform             = $Platform
        }
        $grant | ConvertTo-Json | Set-Content -LiteralPath $grantPath -Encoding utf8
    }
    Write-Output "Revoked $Platform session."
    Write-Output "  stored credential removed: $credentialTarget"
    Write-Output "  grant disabled: $grantPath"
    return
}

# --- validate the cookie shape before touching storage ------------------------------------------------
if ($Cookie.Length -gt 8192) { throw 'The cookie value is longer than the 8192-character limit.' }
if ($Cookie -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') { throw 'The cookie contains control characters.' }
if ($Cookie -notmatch '=') { throw 'That does not look like a Cookie header value (expected name=value pairs).' }
if ($Cookie -match '(?i)(password|passwd|pwd)\s*=') {
    throw 'Store the session cookie only, never a password. Remove the password field and retry.'
}
$null = Add-Type -AssemblyName System.Text.Json -ErrorAction SilentlyContinue

# --- 1. secret into Windows Credential Manager -------------------------------------------------------
[SocialSessionCredential]::Save($credentialTarget, 'session', $Cookie)

# --- 2. grant file (contains no secret) --------------------------------------------------------------
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
$revision = Read-Revision -Path $grantPath
# 小红书 session search needs the web client's request signature, which this build does not produce yet.
# The cookie is still stored so it survives until the capability exists, but the grant stays disabled:
# enabling it would only send requests that are certain to be refused.
$sessionSearchSupported = $Platform -ne 'xiaohongshu'
$grant = [ordered]@{
    Enabled              = $sessionSearchSupported
    AuthorizationId      = [Guid]::NewGuid().ToString('N')
    AuthorizationVersion = $revision
    Platform             = $Platform
}
$grant | ConvertTo-Json | Set-Content -LiteralPath $grantPath -Encoding utf8

Write-Output "Configured $Platform session (cookie stored)."
Write-Output "  credential target : $credentialTarget"
if ($sessionSearchSupported) {
    Write-Output "  grant file        : $grantPath (enabled, revision $revision, contains no secret)"
    Write-Output ''
    Write-Output 'Restart the app if it is running. The host then confirms the connection with:'
    Write-Output "  plugin.CheckSessionAsync('$Platform', token)"
    Write-Output 'An expired cookie reports SOCIAL_SESSION_EXPIRED and is never retried automatically.'
}
else {
    Write-Output "  grant file        : $grantPath (disabled, revision $revision, contains no secret)"
    Write-Output ''
    Write-Output "This build cannot sign $Platform requests yet (the endpoint needs the web client's x-s /"
    Write-Output 'x-s-common / x-t / x-rap-param headers), so the session search stays off. The stored cookie is'
    Write-Output 'kept for when that capability lands; no request is sent in the meantime.'
}
