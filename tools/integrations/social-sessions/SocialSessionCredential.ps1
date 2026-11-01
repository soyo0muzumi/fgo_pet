# Shared helpers for the social-session scripts. Dot-sourced; not a standalone entry point.
#
# The Credential Manager interop below is deliberately identical to the application's own
# WindowsCredentialStore: CRED_TYPE_GENERIC (1), CRED_PERSIST_LOCAL_MACHINE (2), UTF-16 blob.

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

# Required names are the login token, verified against the live endpoints. Advisory names are useful
# but their exact spelling was not verified for every platform, so they never block a save.
#   bilibili    -101 (no login) vs -100 (expired) proves SESSDATA is the token
#   xiaohongshu a forged web_session switches -101 to -100 (stale), and a live session without the
#               web client's request signature is answered 300011, so web_session is the token either way
#   xiaoheihe   no anonymously observable cookie names; nothing is required by name
#
# SessionSearchSupported is false while this build cannot produce the request shape a platform requires.
# The cookie is still validated and stored (so it survives until the capability exists), but the grant is
# written disabled: enabling a session search that can only be refused would just send failing requests.
$script:SocialPlatforms = @{
    bilibili    = @{ Key = 'BILIBILI_COOKIE'
                     Required = @('SESSDATA')
                     Advisory = @('DedeUserID')
                     SessionSearchSupported = $true }
    xiaohongshu = @{ Key = 'XIAOHONGSHU_COOKIE'
                     Required = @('web_session')
                     Advisory = @('a1')
                     SessionSearchSupported = $false }
    xiaoheihe   = @{ Key = 'XIAOHEIHE_COOKIE'
                     Required = @()
                     Advisory = @('heybox_id', 'pkey')
                     SessionSearchSupported = $true }
}

function Test-SessionSearchSupported {
    param([hashtable] $Spec)
    return -not $Spec.ContainsKey('SessionSearchSupported') -or [bool]$Spec.SessionSearchSupported
}

function Resolve-SocialStateRoot {
    param([string] $StateRoot)
    if ($StateRoot) {
        if (-not [IO.Path]::IsPathFullyQualified($StateRoot)) { throw "State root must be absolute: $StateRoot" }
        return [IO.Path]::GetFullPath($StateRoot)
    }
    $root = if ($env:FGO_PET_STATE_ROOT) {
        $env:FGO_PET_STATE_ROOT
    } else {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'FgoPet'
    }
    if (-not [IO.Path]::IsPathFullyQualified($root)) { throw "State root must be absolute: $root" }
    return [IO.Path]::GetFullPath($root)
}

# Cookie NAMES only. Never returns or prints values.
function Get-CookieNames {
    param([string] $Cookie)
    return @($Cookie.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { ($_ -split '=', 2)[0].Trim() } | Where-Object { $_ })
}

function Test-CookieShape {
    param([string] $Platform, [string] $Cookie)
    if ($Cookie.Length -gt 8192) { return 'cookie is longer than the 8192-character limit' }
    if ($Cookie -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') { return 'cookie contains control characters' }
    if ($Cookie -notmatch '=') { return 'not a Cookie header value (expected name=value pairs)' }
    if ($Cookie -match '(?i)(password|passwd|pwd)\s*=') { return 'contains a password field; store the session cookie only' }
    if ($Cookie -match '[\r\n]') { return 'cookie spans multiple lines; keep it on one line' }
    return $null
}

function Write-SocialGrant {
    param([string] $Platform, [bool] $Enabled, [string] $StateRoot)

    $grantPath = Join-Path $StateRoot "social-session-$Platform.json"
    $revision = 1
    if (Test-Path -LiteralPath $grantPath) {
        try {
            $previous = Get-Content -LiteralPath $grantPath -Raw | ConvertFrom-Json
            if ($previous.AuthorizationVersion -and [long]$previous.AuthorizationVersion -gt 0) {
                $revision = [long]$previous.AuthorizationVersion + 1
            }
        } catch { }
    }
    New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
    $grant = [ordered]@{
        Enabled              = $Enabled
        AuthorizationId      = [Guid]::NewGuid().ToString('N')
        AuthorizationVersion = $revision
        Platform             = $Platform
    }
    $grant | ConvertTo-Json | Set-Content -LiteralPath $grantPath -Encoding utf8
    return [pscustomobject]@{ Path = $grantPath; Revision = $revision }
}

function Read-SocialEnvFile {
    param([string] $Path)
    $values = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $values }
    foreach ($raw in Get-Content -LiteralPath $Path) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { continue }
        $name = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}
