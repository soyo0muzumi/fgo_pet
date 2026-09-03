[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$CandidateRoot,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$TempRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')

function Resolve-IsolatedRoot {
    param([string]$Value, [string]$Label)
    try { $full = [IO.Path]::GetFullPath($Value) } catch { throw "$Label is not a valid path." }
    $root = [IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($root) -or $full.TrimEnd('\', '/') -eq $root.TrimEnd('\', '/')) {
        throw "$Label must be below a filesystem root."
    }
    if ($full.TrimEnd('\', '/') -eq $repositoryRoot -or $full.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must not resolve to the repository or one of its children."
    }
    $probe = $full
    while ($null -ne $probe -and $probe -ne [IO.Path]::GetPathRoot($probe)) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label must not use an existing junction or symlink." }
        }
        $probe = Split-Path -Parent $probe
    }
    return $full.TrimEnd('\', '/')
}

function Assert-PathInside {
    param([string]$Path, [string]$Parent)
    if (-not $Path.StartsWith($Parent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated path is outside TempRoot: $Path"
    }
}

$candidate = Resolve-IsolatedRoot $CandidateRoot 'CandidateRoot'
$temp = Resolve-IsolatedRoot $TempRoot 'TempRoot'
$generated = Join-Path $temp ('release-acceptance-' + [guid]::NewGuid().ToString('N'))
Assert-PathInside $generated $temp
[IO.Directory]::CreateDirectory($generated) | Out-Null
$gateError = $null

try {
    $verify = Join-Path $PSScriptRoot 'verify-release.ps1'
    $install = Join-Path $PSScriptRoot 'install-codex-adapter.ps1'
    $uninstall = Join-Path $PSScriptRoot 'uninstall-codex-adapter.ps1'
    & pwsh -NoProfile -File $verify -CandidateRoot $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }

    $archive = @(Get-ChildItem -LiteralPath (Join-Path $candidate 'app') -Filter 'FgoPet-win-x64-*.zip' -File)
    if ($archive.Count -ne 1) { throw 'Candidate must contain exactly one App archive.' }
    $extract = Join-Path $generated 'extracted-app'
    $installRoot = Join-Path $generated 'adapter-install'
    $codexHome = Join-Path $generated 'codex-home'
    $stateRoot = Join-Path $generated 'state'
    foreach ($path in @($extract, $installRoot, $codexHome, $stateRoot)) { Assert-PathInside $path $temp }
    Expand-Archive -LiteralPath $archive[0].FullName -DestinationPath $extract -Force
    foreach ($required in @('FgoPet.App.exe', 'FgoPet.AgentRelay.exe', 'FgoPet.CodexAdapter.exe')) {
        if (-not (Get-ChildItem -LiteralPath $extract -Filter $required -File -Recurse)) { throw "Extracted archive is missing $required." }
    }
    $appDirectory = Join-Path $candidate 'app'
    $roleFiles = @(Get-ChildItem -LiteralPath $candidate -Filter '*.fgopetpack' -File -Recurse)
    foreach ($roleFile in $roleFiles) {
        if ($roleFile.FullName.StartsWith($appDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Role-package output must remain outside the App output root.'
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive[0].FullName)
    try { if (@($zip.Entries | Where-Object { $_.FullName -like '*.fgopetpack' }).Count -gt 0) { throw 'Role packages must not be embedded in the App archive.' } }
    finally { $zip.Dispose() }

    $common = @('-InstallRoot', $installRoot, '-CodexHome', $codexHome, '-PublishedSource', $extract,
        '-SkipUserPath', '-SkipPluginRegistration')
    # Keep the assertion outside the installer-owned state directories, which
    # uninstall-codex-adapter.ps1 deliberately removes.
    $sentinel = Join-Path $stateRoot 'acceptance-preserve.txt'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $sentinel)) | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'acceptance-sentinel' -NoNewline
    $previousStateRoot = [Environment]::GetEnvironmentVariable('FGO_PET_STATE_ROOT', 'Process')
    $previousPipeSuffix = [Environment]::GetEnvironmentVariable('FGO_PET_PIPE_SUFFIX', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('FGO_PET_STATE_ROOT', $stateRoot, 'Process')
        [Environment]::SetEnvironmentVariable('FGO_PET_PIPE_SUFFIX', 'release-acceptance-' + [guid]::NewGuid().ToString('N'), 'Process')
        & pwsh -NoProfile -File $install @common
        if ($LASTEXITCODE -ne 0) { throw 'Isolated adapter install and MCP smoke failed.' }
        if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw 'Upgrade sentinel disappeared after first install.' }
        & pwsh -NoProfile -File $install @common
        if ($LASTEXITCODE -ne 0) { throw 'Upgrade simulation failed.' }
        if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw 'Upgrade simulation did not preserve isolated state.' }
        & pwsh -NoProfile -File $uninstall -InstallRoot $installRoot -CodexHome $codexHome -StateRoot $stateRoot
        if ($LASTEXITCODE -ne 0) { throw 'Isolated adapter uninstall failed.' }
        if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw 'Uninstall did not preserve isolated state.' }
    }
    finally {
        [Environment]::SetEnvironmentVariable('FGO_PET_STATE_ROOT', $previousStateRoot, 'Process')
        [Environment]::SetEnvironmentVariable('FGO_PET_PIPE_SUFFIX', $previousPipeSuffix, 'Process')
    }
    Write-Output 'Release-candidate acceptance passed: verify, extraction, executables, MCP smoke, upgrade, uninstall preservation.'
}
catch {
    $gateError = $_.Exception
    throw
}
finally {
    if (Test-Path -LiteralPath $generated) {
        try { Remove-Item -LiteralPath $generated -Recurse -Force -ErrorAction Stop }
        catch {
            $cleanupMessage = "Generated temporary cleanup failed: $($_.Exception.Message)"
            if ($null -ne $gateError) { throw "$cleanupMessage Original gate error: $($gateError.Message)" }
            throw $cleanupMessage
        }
    }
}
