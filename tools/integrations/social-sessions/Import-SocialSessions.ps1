<#
.SYNOPSIS
    Imports social platform sessions from .env and enables their session grants.

.DESCRIPTION
    Reads BILIBILI_COOKIE / XIAOHONGSHU_COOKIE / XIAOHEIHE_COOKIE from the .env file next to this script
    (or -EnvFile), then for each non-empty value:

      1. writes the cookie into Windows Credential Manager under FgoPet.SocialSources.<platform>
      2. writes social-session-<platform>.json in the app state root (no secret inside)

    Empty or missing keys are skipped, so an unused platform stays unconfigured. Nothing is ever printed
    except platform names, cookie NAMES and lengths — never a cookie value.

    A platform whose session search this build cannot complete yet (currently 小红书, whose endpoint needs
    the web client's request signature) still gets its cookie validated and stored — so it survives until
    the capability exists — but its grant is written disabled. Enabling it would only send requests that
    are certain to be refused.

.PARAMETER EnvFile
    Path to the .env file. Defaults to .env beside this script.

.PARAMETER StateRoot
    Overrides the app state root. Defaults to FGO_PET_STATE_ROOT, else %LOCALAPPDATA%\FgoPet.

.PARAMETER WhatIfOnly
    Validate and report without writing anything.

.EXAMPLE
    pwsh -File tools/integrations/social-sessions/Import-SocialSessions.ps1

.EXAMPLE
    pwsh -File tools/integrations/social-sessions/Import-SocialSessions.ps1 -WhatIfOnly
#>
[CmdletBinding()]
param(
    [string] $EnvFile,
    [string] $StateRoot,
    [switch] $WhatIfOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SocialSessionCredential.ps1')

if (-not $EnvFile) { $EnvFile = Join-Path $PSScriptRoot '.env' }
if (-not (Test-Path -LiteralPath $EnvFile)) {
    throw "No .env found at $EnvFile. Copy .env.example to .env and fill in the cookies."
}
$env = Read-SocialEnvFile -Path $EnvFile
$resolvedRoot = Resolve-SocialStateRoot -StateRoot $StateRoot

Write-Output "env file : $([IO.Path]::GetFullPath($EnvFile))"
Write-Output "state    : $resolvedRoot"
if ($WhatIfOnly) { Write-Output 'mode     : validate only (nothing will be written)' }
Write-Output ''

$configured = 0
$disabled = 0
$skipped = 0
$rejected = 0

foreach ($platform in $script:SocialPlatforms.Keys | Sort-Object) {
    $spec = $script:SocialPlatforms[$platform]
    $cookie = $env[$spec.Key]

    if ([string]::IsNullOrWhiteSpace($cookie)) {
        Write-Output ("{0,-12} skip   ({1} is empty)" -f $platform, $spec.Key)
        $skipped++
        continue
    }

    $shapeError = Test-CookieShape -Platform $platform -Cookie $cookie
    if ($shapeError) {
        Write-Output ("{0,-12} REJECT {1}" -f $platform, $shapeError)
        $rejected++
        continue
    }

    $names = Get-CookieNames -Cookie $cookie
    $missingRequired = @($spec.Required | Where-Object { $names -notcontains $_ })
    $missingAdvisory = @($spec.Advisory | Where-Object { $names -notcontains $_ })
    $supported = Test-SessionSearchSupported -Spec $spec

    $detail = "{0} chars, {1} cookie names" -f $cookie.Length, $names.Count

    if ($missingRequired.Count -gt 0) {
        # A missing login token means the paste is truncated or the tab is not signed in. Refuse rather
        # than store something that will only fail later with a confusing error.
        Write-Output ("{0,-12} REJECT {1} (missing login token: {2})" -f `
            $platform, $detail, ($missingRequired -join ', '))
        $rejected++
        continue
    }
    if ($missingAdvisory.Count -gt 0) {
        $detail += " (not present, continuing: $($missingAdvisory -join ', '))"
    }

    if ($WhatIfOnly) {
        Write-Output ("{0,-12} ok     {1}" -f $platform, $detail)
        if (-not $supported) { Write-Output ("             grant would stay disabled: $platform session search is not supported yet") }
        $configured++
        continue
    }

    $target = "FgoPet.SocialSources.$platform"
    [SocialSessionCredential]::Save($target, 'session', $cookie)
    $grant = Write-SocialGrant -Platform $platform -Enabled $supported -StateRoot $resolvedRoot
    Write-Output ("{0,-12} saved  {1}" -f $platform, $detail)
    Write-Output ("             credential={0} grant={1} revision={2}" -f $target, $grant.Path, $grant.Revision)
    if ($supported) {
        $configured++
    }
    else {
        Write-Output ("             grant stays disabled: $platform session search is not supported yet")
        $disabled++
        $configured++
    }
}

Write-Output ''
Write-Output "configured=$configured skipped=$skipped rejected=$rejected"
if ($disabled -gt 0) {
    Write-Output "session stored but left disabled: $disabled platform(s) — a supported cookie is kept for later."
}
if ($rejected -gt 0) {
    Write-Output ''
    Write-Output 'A rejected platform stored nothing. Check that the cookie came from the Request Headers'
    Write-Output 'Cookie value (not the Application tab) and was copied in full.'
    exit 2
}
if ($WhatIfOnly) { exit 0 }
Write-Output ''
Write-Output 'Restart the app if it is running, then confirm from the host side:'
Write-Output "  plugin.CheckSessionAsync('<platform>', token)"
