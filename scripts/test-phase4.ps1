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
$invocationId = [guid]::NewGuid().ToString('N')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('fgo-pet-phase4-' + $invocationId)
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$originalUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$resultsDirectory = Join-Path $repositoryRoot ('artifacts\validation\phase4-' + $invocationId)
$publishRoot = $null
$installRoot = Join-Path $temporaryRoot 'install'
$testCodexHome = Join-Path $temporaryRoot 'codex-home'
$testStateRoot = Join-Path $temporaryRoot 'retained-state'
$retainedFiles = @{}
$testProjectCount = 0

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
        Write-Warning 'Codex plugin-creator validator was not found; fallback manifest validation passed.'
        return
    }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python is required for the Codex plugin validator.' }
    Invoke-Checked -Command 'python' -Arguments @($validator, $pluginRoot)
}

function Assert-RetainedAcceptanceFiles {
    foreach ($path in $retainedFiles.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $retainedFiles[$path]) {
            throw 'An isolated configuration, state or unowned-file fixture was changed.'
        }
    }
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -cne $originalUserPath) {
        throw 'The isolated gate changed user PATH.'
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $env:TEMP = $temporaryRoot
    $env:TMP = $temporaryRoot
    Push-Location $repositoryRoot
    try {
        if (-not $SkipBuild) {
            Invoke-Checked -Command 'dotnet' -Arguments @('restore', $solution)
            Invoke-Checked -Command 'dotnet' -Arguments @('build', $solution, '-c', 'Release', '--no-restore', '-warnaserror')
            # All solution test projects follow the .Tests naming convention.
            # Execute each exactly once, with its own process-inherited TEMP/TMP.
            # The acceptance workflow independently compares the resulting full
            # project/case manifest; no test filter or xUnit parallelism override.
            $testProjects = @(Select-String -LiteralPath $solution -Pattern '^Project\("[^"]+"\) = "([^"]+\.Tests)", "([^"]+\.csproj)"' |
                ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $_.Matches[0].Groups[2].Value)) })
            if ($testProjects.Count -eq 0 -or @($testProjects | Sort-Object -Unique).Count -ne $testProjects.Count) {
                throw 'The solution must contain a nonempty, unique test-project list.'
            }
            foreach ($project in $testProjects) {
                $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
                # The invocation GUID already provides uniqueness. Keep the child
                # short so nested backup staging paths remain usable by native IO.
                $projectTemp = Join-Path $temporaryRoot ('t' + $testProjectCount.ToString('00'))
                if (Test-Path -LiteralPath $projectTemp) { throw 'Refusing to reuse a project temporary directory.' }
                [IO.Directory]::CreateDirectory($projectTemp) | Out-Null
                $env:TEMP = $projectTemp
                $env:TMP = $projectTemp
                try {
                    Invoke-Checked -Command 'dotnet' -Arguments @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '-m:1', '--logger', "trx;LogFileName=$projectName.trx", '--results-directory', $resultsDirectory)
                    $testProjectCount++
                }
                finally {
                    $env:TEMP = $temporaryRoot
                    $env:TMP = $temporaryRoot
                    # Only this invocation's exact generated child is removed.
                    # A locked file is a gate failure, not a swallowed IO error.
                    Remove-Item -LiteralPath $projectTemp -Recurse -Force -ErrorAction Stop
                }
            }
        }
        else { Write-Host 'Skipped restore, build, and solution tests by request.' }

        Invoke-PluginValidation

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

        # Synthetic sentinels only: never read or copy real Codex/pairing state.
        foreach ($path in @(
            (Join-Path $installRoot 'unowned-acceptance.txt'),
            (Join-Path $testCodexHome 'config.toml'),
            (Join-Path $testStateRoot 'AgentRelay\acceptance.txt'),
            (Join-Path $testStateRoot 'CodexAdapter\acceptance.txt')
        )) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.File]::WriteAllText($path, '# isolated acceptance fixture', [Text.UTF8Encoding]::new($false))
            $retainedFiles[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }

        & (Join-Path $repositoryRoot 'scripts\install-codex-adapter.ps1') `
            -InstallRoot $installRoot -CodexHome $testCodexHome -SkipUserPath `
            -SkipPluginRegistration -SkipBuild -PublishedSource $PublishedSource
        if ($LASTEXITCODE -ne 0) { throw "Isolated installer failed with exit code $LASTEXITCODE." }

        $markerPath = Join-Path $installRoot '.fgo-pet-codex-adapter.install.json'
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.SchemaVersion -ne 1 -or $marker.PathEntryAdded -or
            $marker.PluginRegistered -or $marker.PluginAddedByInstaller -or $marker.MarketplaceAddedByInstaller) {
            throw 'The isolated installer marker has unexpected ownership or registration flags.'
        }
        $requiredFiles = @('FgoPet.CodexAdapter.exe', 'FgoPet.AgentRelay.exe', 'fgo-pet-codex-adapter.cmd')
        foreach ($name in $requiredFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $installRoot $name) -PathType Leaf)) {
                throw "The installed package is missing $name."
            }
            $record = @($marker.Files | Where-Object { $_.RelativePath -eq $name })
            if ($record.Count -ne 1 -or $record[0].ExistedBefore -or
                (Get-FileHash -LiteralPath (Join-Path $installRoot $name) -Algorithm SHA256).Hash -ne $record[0].InstalledHash) {
                throw "The installed ownership or hash is invalid for $name."
            }
        }
        Assert-RetainedAcceptanceFiles

        & (Join-Path $repositoryRoot 'scripts\uninstall-codex-adapter.ps1') `
            -InstallRoot $installRoot -CodexHome $testCodexHome -StateRoot $testStateRoot
        if ($LASTEXITCODE -ne 0) { throw "Isolated uninstaller failed with exit code $LASTEXITCODE." }
        foreach ($name in $requiredFiles + @('.fgo-pet-codex-adapter.install.json')) {
            if (Test-Path -LiteralPath (Join-Path $installRoot $name)) {
                throw "The isolated uninstaller left an owned file: $name."
            }
        }
        Assert-RetainedAcceptanceFiles
        Write-Host 'Phase 4 install, MCP smoke, uninstall and synthetic state-preservation checks passed.' -ForegroundColor Green
    }
    finally { Pop-Location }
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
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
}

# Reached only after all assertions AND strict cleanup succeeded. A skipped-build
# invocation cannot be mistaken for a full solution run by an acceptance caller.
[IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
[ordered]@{
    schemaVersion = 1
    invocationId = $invocationId
    completedUtc = [DateTime]::UtcNow.ToString('O')
    fullSolutionTests = -not [bool]$SkipBuild
    isolatedTestProjects = $testProjectCount
    packagingVerified = $true
    syntheticStatePreserved = $true
    userPathUnchanged = $true
    temporaryRootRemoved = -not (Test-Path -LiteralPath $temporaryRoot)
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultsDirectory 'phase4-summary.json') -Encoding utf8
Write-Host 'Phase 4 automated packaging gate passed.' -ForegroundColor Green
