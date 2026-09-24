[CmdletBinding()]
param(
    # Supplying a pre-published directory lets a Release build/publish be
    # coordinated by the caller without making this script publish twice.
    [string]$PublishedSource,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginRoot = Join-Path $repositoryRoot 'integrations\codex\fgo-pet-agent'
$adapterProject = Join-Path $repositoryRoot 'modules\agent-integration\src\FgoPet.CodexAdapter\FgoPet.CodexAdapter.csproj'
$relayProject = Join-Path $repositoryRoot 'modules\agent-integration\src\FgoPet.AgentRelay\FgoPet.AgentRelay.csproj'
$solution = Join-Path $repositoryRoot 'FgoPet.sln'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('fgo-pet-phase4-' + [guid]::NewGuid().ToString('N'))
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$resultsDirectory = Join-Path $repositoryRoot ('artifacts\validation\phase4-' + [guid]::NewGuid().ToString('N'))
$publishRoot = $null
$installRoot = Join-Path $temporaryRoot 'install'
$testCodexHome = Join-Path $temporaryRoot 'codex-home'
$testStateRoot = Join-Path $temporaryRoot 'state'
$report = [ordered]@{
    SchemaVersion = 1
    RunId = [guid]::NewGuid().ToString('N')
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    CompletedUtc = $null
    Outcome = 'failed'
    LastStage = 'initialization'
    FullSolutionPassed = $false
    PublishedSourceSupplied = -not [string]::IsNullOrWhiteSpace($PublishedSource)
    PluginValidation = 'not-run'
    PackagingPassed = $false
    SyntheticStatePreserved = $false
    UnownedFilePreserved = $false
    TemporaryRootCleaned = $false
}
$gateSucceeded = $false
[IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null

function Set-Phase4Stage {
    param([Parameter(Mandatory = $true)][string]$Stage)
    $report.LastStage = $Stage
    Write-Host (@{ utc = [DateTime]::UtcNow.ToString('O'); run = $report.RunId; stage = $Stage; outcome = 'started' } | ConvertTo-Json -Compress)
}

function Invoke-Checked {
    param([Parameter(Mandatory = $true)][string]$Command, [Parameter(Mandatory = $true)][string[]]$Arguments)
    if ($Command -eq 'dotnet') {
        # Reused build servers retain their first TEMP path and can recreate a
        # previous run's NuGetScratch after cleanup. Keep servers invocation-local.
        $Arguments += '--disable-build-servers'
    }
    Write-Host (">> {0} {1}" -f $Command, ($Arguments -join ' ')) -ForegroundColor Cyan
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Invoke-PluginValidation {
    $validatorCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $validatorCandidates += Join-Path $env:USERPROFILE '.codex\skills\.system\plugin-creator\scripts\validate_plugin.py'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $validatorCandidates += Join-Path $env:CODEX_HOME 'skills\.system\plugin-creator\scripts\validate_plugin.py'
    }
    $validator = $validatorCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $validator) {
        # Keep the gate useful on a clean checkout while retaining the strict
        # external validator when the Codex skill installation provides it.
        $manifest = Get-Content -LiteralPath (Join-Path $pluginRoot '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
        $mcp = Get-Content -LiteralPath (Join-Path $pluginRoot '.mcp.json') -Raw | ConvertFrom-Json
        if ($manifest.name -ne 'fgo-pet-agent' -or $null -eq $mcp.mcpServers.'fgo-pet-agent') {
            throw 'The fallback Codex plugin manifest validation failed.'
        }
        $report.PluginValidation = 'manifest-fallback'
        Write-Warning 'Codex plugin-creator validator was not found; fallback manifest validation passed.'
        return
    }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python is required for the Codex plugin validator.' }
    Invoke-Checked -Command 'python' -Arguments @($validator, $pluginRoot)
    $report.PluginValidation = 'external-validator'
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    # Children inherit one invocation-owned temp root; each test fixture/process
    # adds its own unique suffix. Never reuse a previous run's temporary files.
    $env:TEMP = $temporaryRoot
    $env:TMP = $temporaryRoot
    Push-Location $repositoryRoot
    try {
        if (-not $SkipBuild) {
            Set-Phase4Stage 'restore'
            Invoke-Checked -Command 'dotnet' -Arguments @('restore', $solution)
            Set-Phase4Stage 'build'
            Invoke-Checked -Command 'dotnet' -Arguments @('build', $solution, '-c', 'Release', '--no-restore', '-warnaserror')
            Set-Phase4Stage 'solution-tests'
            # Serialize project test hosts, not xUnit collections or business concurrency scenarios.
            # Keep this aligned with the full-solution CI to avoid WPF/output/temp contention.
            Invoke-Checked -Command 'dotnet' -Arguments @('test', $solution, '-c', 'Release', '--no-build', '--no-restore', '-m:1', '--logger', 'trx', '--results-directory', $resultsDirectory)
            $report.FullSolutionPassed = $true
        }
        else { Write-Host 'Skipped restore, build, and solution tests by request.' }

        Set-Phase4Stage 'plugin-validation'
        Invoke-PluginValidation

        Set-Phase4Stage 'publish'
        if ([string]::IsNullOrWhiteSpace($PublishedSource)) {
            # The solution restore does not create RID-specific assets needed by
            # the isolated win-x64 publish below. Restore the two published
            # projects explicitly while keeping the normal solution gate above.
            Invoke-Checked -Command 'dotnet' -Arguments @('restore', $adapterProject, '-r', 'win-x64')
            Invoke-Checked -Command 'dotnet' -Arguments @('restore', $relayProject, '-r', 'win-x64')
            $publishRoot = Join-Path $temporaryRoot 'published'
            $adapterOut = Join-Path $publishRoot 'adapter'
            $relayOut = Join-Path $publishRoot 'relay'
            [IO.Directory]::CreateDirectory($adapterOut) | Out-Null
            [IO.Directory]::CreateDirectory($relayOut) | Out-Null
            Invoke-Checked -Command 'dotnet' -Arguments @('publish', $adapterProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--no-restore', '-o', $adapterOut)
            Invoke-Checked -Command 'dotnet' -Arguments @('publish', $relayProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--no-restore', '-o', $relayOut)
            $PublishedSource = $publishRoot
        }
        else { $PublishedSource = [IO.Path]::GetFullPath($PublishedSource) }

        # Synthetic sentinels only: never use real pairing data or an existing user installation.
        Set-Phase4Stage 'install-and-mcp-smoke'
        [IO.Directory]::CreateDirectory($installRoot) | Out-Null
        $unownedFile = Join-Path $installRoot 'user-owned.fixture'
        [IO.File]::WriteAllText($unownedFile, 'retain-unowned-file')
        $stateFiles = foreach ($component in @('AgentRelay', 'CodexAdapter')) {
            $directory = Join-Path $testStateRoot $component
            [IO.Directory]::CreateDirectory($directory) | Out-Null
            $path = Join-Path $directory 'state.fixture'
            [IO.File]::WriteAllText($path, 'retain-synthetic-state')
            $path
        }
        & (Join-Path $repositoryRoot 'scripts\install-codex-adapter.ps1') `
            -InstallRoot $installRoot -CodexHome $testCodexHome -SkipUserPath `
            -SkipPluginRegistration -SkipBuild -PublishedSource $PublishedSource
        if ($LASTEXITCODE -ne 0) { throw "Isolated installer failed with exit code $LASTEXITCODE." }

        $markerPath = Join-Path $installRoot '.fgo-pet-codex-adapter.install.json'
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.SchemaVersion -ne 1 -or @($marker.Files).Count -eq 0 -or
            $marker.PathEntryAdded -or $marker.PluginRegistered) {
            throw 'The isolated installation marker is missing, invalid, or claims external registration.'
        }
        foreach ($name in @('FgoPet.CodexAdapter.exe', 'FgoPet.AgentRelay.exe', 'fgo-pet-codex-adapter.cmd')) {
            if (-not (Test-Path -LiteralPath (Join-Path $installRoot $name) -PathType Leaf)) {
                throw 'An installed entry point is missing.'
            }
        }
        $ownedPaths = foreach ($file in @($marker.Files)) {
            $path = [IO.Path]::GetFullPath((Join-Path $installRoot ([string]$file.RelativePath)))
            if (-not $path.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                $file.ExistedBefore -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw 'The isolated installation has an invalid owned-file record.'
            }
            $path
        }
        Set-Phase4Stage 'uninstall-and-preservation'
        & (Join-Path $repositoryRoot 'scripts\uninstall-codex-adapter.ps1') `
            -InstallRoot $installRoot -CodexHome $testCodexHome -StateRoot $testStateRoot
        if ($LASTEXITCODE -ne 0) { throw "Isolated uninstaller failed with exit code $LASTEXITCODE." }
        # Assert before the outer cleanup, which must not hide an ineffective uninstaller.
        foreach ($path in @($ownedPaths) + @($markerPath)) {
            if (Test-Path -LiteralPath $path) { throw 'An installer-owned file remains after uninstall.' }
        }
        if ([IO.File]::ReadAllText($unownedFile) -ne 'retain-unowned-file') {
            throw 'Uninstall changed an unowned file.'
        }
        $report.UnownedFilePreserved = $true
        foreach ($path in $stateFiles) {
            if ([IO.File]::ReadAllText($path) -ne 'retain-synthetic-state') {
                throw 'Uninstall changed synthetic pairing state without explicit removal.'
            }
        }
        $report.SyntheticStatePreserved = $true
        $report.PackagingPassed = $true
    }
    finally { Pop-Location }
    $gateSucceeded = $true
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    try {
        if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
            # This path is generated by this invocation and never points at a user
            # home or repository directory.
            $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
            $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
            if ([IO.Path]::GetDirectoryName($resolvedTemporaryRoot).TrimEnd('\', '/') -ne $expectedParent -or
                [IO.Path]::GetFileName($resolvedTemporaryRoot) -notmatch '^fgo-pet-phase4-[a-f0-9]{32}$') {
                throw 'Refusing to clean an unexpected Phase 4 temporary path.'
            }
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction Stop
        }
        $report.TemporaryRootCleaned = $true
    }
    finally {
        # Fixed stages and booleans only: no exception text, paths, input, or credentials.
        $report.CompletedUtc = [DateTime]::UtcNow.ToString('O')
        if ($gateSucceeded -and $report.TemporaryRootCleaned) { $report.Outcome = 'passed' }
        [IO.File]::WriteAllText((Join-Path $resultsDirectory 'phase4-summary.json'),
            ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-Host (@{ utc = $report.CompletedUtc; run = $report.RunId; stage = $report.LastStage; outcome = $report.Outcome } | ConvertTo-Json -Compress)
    }
}
Write-Host 'Phase 4 automated packaging gate passed.' -ForegroundColor Green
