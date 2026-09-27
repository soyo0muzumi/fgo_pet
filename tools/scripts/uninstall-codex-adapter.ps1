[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$CodexHome,
    [string]$StateRoot,
    [switch]$RemoveState
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$markerName = '.fgo-pet-codex-adapter.install.json'
$pluginName = 'fgo-pet-agent'
$marketplaceName = 'fgo-pet-local'

function Resolve-SafeDirectory {
    param([Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Label)
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "$Label must not be empty." }
    try { $full = [IO.Path]::GetFullPath($Value) } catch { throw "$Label is not a valid path." }
    $root = [IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($root) -or $full.TrimEnd('\', '/') -eq $root.TrimEnd('\', '/')) {
        throw "$Label must name a directory below a filesystem root."
    }
    return $full.TrimEnd('\', '/')
}

function Get-DefaultInstallRoot {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) { $localAppData = $env:LOCALAPPDATA }
    if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'Local application data is unavailable; pass -InstallRoot explicitly.' }
    return Join-Path $localAppData 'FgoPet\bin'
}

function Get-DefaultCodexHome {
    $configured = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
    if ([string]::IsNullOrWhiteSpace($configured)) { $configured = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'User') }
    if (-not [string]::IsNullOrWhiteSpace($configured)) { return $configured }
    $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($profile)) { throw 'The user profile is unavailable; pass -CodexHome explicitly.' }
    return Join-Path $profile '.codex'
}

function Get-FileHashHex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-CodexBestEffort {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & codex @Arguments 2>&1 | Out-Host
    return $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-DefaultInstallRoot }
$installDirectory = Resolve-SafeDirectory -Value $InstallRoot -Label 'InstallRoot'
$markerPath = Join-Path $installDirectory $markerName
$marker = $null
if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    try { $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json }
    catch { throw "The installer marker is corrupt; refusing to remove untracked files: $markerPath" }
    if ($marker.SchemaVersion -ne 1 -or ([IO.Path]::GetFullPath([string]$marker.InstallRoot)).TrimEnd('\', '/') -ne $installDirectory) {
        throw 'The installer marker does not belong to the requested InstallRoot.'
    }
}

if ($null -ne $marker) {
    foreach ($file in @($marker.Files)) {
        $relative = [string]$file.RelativePath
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $installDirectory $relative))
        if (-not $target.StartsWith($installDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $keepExisting = [bool]$file.ExistedBefore
        $unchanged = $false
        try { $unchanged = [StringComparer]::OrdinalIgnoreCase.Equals((Get-FileHashHex -Path $target), [string]$file.InstalledHash) } catch { }
        if (-not $keepExisting -and $unchanged) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    if ([bool]$marker.PathEntryAdded) {
        $currentPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not [string]::IsNullOrWhiteSpace($currentPath)) {
            $remaining = @($currentPath -split ';' | Where-Object {
                if ([string]::IsNullOrWhiteSpace($_)) { return $false }
                try { $candidate = ([IO.Path]::GetFullPath($_.Trim())).TrimEnd('\', '/') } catch { $candidate = $_.Trim() }
                return -not [StringComparer]::OrdinalIgnoreCase.Equals($candidate, $installDirectory)
            })
            [Environment]::SetEnvironmentVariable('Path', ($remaining -join ';'), 'User')
            Write-Host 'Removed the adapter directory from the user PATH.'
        }
    }
}

$markerCodexHome = if ($null -ne $marker -and $marker.PSObject.Properties.Name -contains 'CodexHome') { [string]$marker.CodexHome } else { $null }
$pluginAddedByInstaller = $null -ne $marker -and $marker.PSObject.Properties.Name -contains 'PluginAddedByInstaller' -and [bool]$marker.PluginAddedByInstaller
if (-not [string]::IsNullOrWhiteSpace($CodexHome) -and -not [string]::IsNullOrWhiteSpace($markerCodexHome)) {
    if ([IO.Path]::GetFullPath($CodexHome).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($markerCodexHome).TrimEnd('\', '/')) {
        throw 'CodexHome does not match the CodexHome recorded by the installer marker.'
    }
}
$codexHomePath = if (-not [string]::IsNullOrWhiteSpace($markerCodexHome)) { $markerCodexHome } elseif ([string]::IsNullOrWhiteSpace($CodexHome)) { Get-DefaultCodexHome } else { $CodexHome }
$codexHomePath = Resolve-SafeDirectory -Value $codexHomePath -Label 'CodexHome'
if ($pluginAddedByInstaller -and (Get-Command codex -ErrorAction SilentlyContinue)) {
    $previous = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('CODEX_HOME', $codexHomePath, 'Process')
        $removeCode = Invoke-CodexBestEffort -Arguments @('plugin', 'remove', "$pluginName@$marketplaceName", '--json')
        if ($removeCode -ne 0) { throw "Codex plugin removal failed with exit code $removeCode." }
        if ($marker.PSObject.Properties.Name -contains 'MarketplaceAddedByInstaller' -and [bool]$marker.MarketplaceAddedByInstaller) {
            $marketplaceCode = Invoke-CodexBestEffort -Arguments @('plugin', 'marketplace', 'remove', $marketplaceName, '--json')
            if ($marketplaceCode -ne 0) { throw "Codex marketplace removal failed with exit code $marketplaceCode." }
        }
    }
    finally { [Environment]::SetEnvironmentVariable('CODEX_HOME', $previous, 'Process') }
}
elseif ($pluginAddedByInstaller) {
    Write-Warning 'Codex was not found; plugin registration was left untouched.'
}

if (Test-Path -LiteralPath $markerPath -PathType Leaf) { Remove-Item -LiteralPath $markerPath -Force }

if ($RemoveState) {
    if ([string]::IsNullOrWhiteSpace($StateRoot)) {
        $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($localAppData)) { $localAppData = $env:LOCALAPPDATA }
        if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'Local application data is unavailable; pass -StateRoot explicitly.' }
        $StateRoot = Join-Path $localAppData 'FgoPet'
    }
    $stateDirectory = Resolve-SafeDirectory -Value $StateRoot -Label 'StateRoot'
    foreach ($ownedStateDirectory in @('AgentRelay', 'CodexAdapter')) {
        $target = Join-Path $stateDirectory $ownedStateDirectory
        if (Test-Path -LiteralPath $target -PathType Container) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
    Write-Host 'Removed only the FGO Pet Relay and Codex adapter state directories.'
}
else { Write-Host 'Pairing state was preserved. Pass -RemoveState only when deliberate state removal is required.' }

Write-Host "FGO Pet Codex adapter uninstalled from $installDirectory."
