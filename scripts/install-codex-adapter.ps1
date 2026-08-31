[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$CodexHome,
    [switch]$SkipUserPath,
    [switch]$SkipPluginRegistration,
    [switch]$SkipBuild,
    [string]$PublishedSource,
    # Names used by the first draft of the installer are retained as aliases so
    # isolated acceptance harnesses can move forward without touching user PATH.
    [Alias('SkipPathUpdate')]
    [switch]$LegacySkipPathUpdate,
    [Alias('SkipPluginInstall')]
    [switch]$LegacySkipPluginInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginRoot = Join-Path $repositoryRoot 'integrations\codex\fgo-pet-agent'
$marketplaceRoot = Join-Path $repositoryRoot 'integrations\codex'
$marketplaceFile = Join-Path $marketplaceRoot '.agents\plugins\marketplace.json'
$markerName = '.fgo-pet-codex-adapter.install.json'
$shimName = 'fgo-pet-codex-adapter.cmd'

function Resolve-SafeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$Create
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label must not be empty."
    }
    try { $full = [IO.Path]::GetFullPath($Value) }
    catch { throw "$Label is not a valid path." }
    $root = [IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($root) -or
        $full.TrimEnd('\', '/') -eq $root.TrimEnd('\', '/')) {
        throw "$Label must name a directory below a filesystem root."
    }
    if ($Create) { [IO.Directory]::CreateDirectory($full) | Out-Null }
    return $full.TrimEnd('\', '/')
}

function Resolve-DefaultInstallRoot {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) { $localAppData = $env:LOCALAPPDATA }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'Local application data is unavailable; pass -InstallRoot explicitly.'
    }
    return Join-Path $localAppData 'FgoPet\bin'
}

function Resolve-DefaultCodexHome {
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

function Get-RelativeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )
    $parentFull = ([IO.Path]::GetFullPath($Parent)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The published file '$Child' is outside its source directory."
    }
    return $childFull.Substring($parentFull.Length)
}

function Assert-PluginPackage {
    if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) { throw "Plugin package is missing: $pluginRoot" }
    if (-not (Test-Path -LiteralPath $marketplaceFile -PathType Leaf)) { throw "Marketplace definition is missing: $marketplaceFile" }
    $manifestPath = Join-Path $pluginRoot '.codex-plugin\plugin.json'
    $mcpPath = Join-Path $pluginRoot '.mcp.json'
    $hooksPath = Join-Path $pluginRoot 'hooks\hooks.json'
    foreach ($required in @($manifestPath, $mcpPath, $hooksPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Plugin package file is missing: $required" }
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $marketplace = Get-Content -LiteralPath $marketplaceFile -Raw | ConvertFrom-Json
        $mcp = Get-Content -LiteralPath $mcpPath -Raw | ConvertFrom-Json
        $hooks = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
    }
    catch { throw 'The Codex plugin package contains invalid JSON.' }
    if ($manifest.name -ne 'fgo-pet-agent' -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        throw 'The Codex plugin manifest has an unexpected name or version.'
    }
    if ($null -eq $mcp.mcpServers.'fgo-pet-agent' -or
        $mcp.mcpServers.'fgo-pet-agent'.command -ne 'fgo-pet-codex-adapter' -or
        $mcp.mcpServers.'fgo-pet-agent'.args[0] -ne 'mcp') {
        throw 'The Codex plugin MCP command must use fgo-pet-codex-adapter mcp.'
    }
    if ($marketplace.name -ne 'fgo-pet-local') { throw 'The local marketplace name is not fgo-pet-local.' }
    $entry = @($marketplace.plugins | Where-Object { $_.name -eq 'fgo-pet-agent' })
    if ($entry.Count -ne 1 -or $entry[0].source.path -ne './fgo-pet-agent') {
        throw 'The local marketplace does not point to the packaged plugin.'
    }
    # Hooks are intentionally deterministic and carry no credential-bearing arguments.
    $hookText = Get-Content -LiteralPath $hooksPath -Raw
    if ($hookText -match '(?i)credential|secret|token') { throw 'Codex hooks must not carry credentials.' }
    return [pscustomobject]@{ PluginName = [string]$manifest.name; MarketplaceName = [string]$marketplace.name }
}

function Find-PublishedDirectories {
    param([Parameter(Mandatory = $true)][string]$Source)
    $sourceFull = Resolve-SafeDirectory -Value $Source -Label 'PublishedSource'
    $adapter = @(Get-ChildItem -LiteralPath $sourceFull -Filter 'FgoPet.CodexAdapter.exe' -File -Recurse)
    $relay = @(Get-ChildItem -LiteralPath $sourceFull -Filter 'FgoPet.AgentRelay.exe' -File -Recurse)
    if ($adapter.Count -ne 1 -or $relay.Count -ne 1) {
        throw "PublishedSource must contain exactly one FgoPet.CodexAdapter.exe and one FgoPet.AgentRelay.exe: $sourceFull"
    }
    $directories = @($adapter[0].DirectoryName, $relay[0].DirectoryName) | Sort-Object -Unique
    return [pscustomobject]@{ Root = $sourceFull; Directories = $directories }
}

function Publish-ReleaseBinaries {
    param([Parameter(Mandatory = $true)][string]$OutputRoot)
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $adapterOut = Join-Path $OutputRoot 'adapter'
    $relayOut = Join-Path $OutputRoot 'relay'
    $adapterProject = Join-Path $repositoryRoot 'src\FgoPet.CodexAdapter\FgoPet.CodexAdapter.csproj'
    $relayProject = Join-Path $repositoryRoot 'src\FgoPet.AgentRelay\FgoPet.AgentRelay.csproj'
    foreach ($publish in @(
        @($adapterProject, $adapterOut),
        @($relayProject, $relayOut)
    )) {
        & dotnet publish $publish[0] -c Release -r win-x64 --self-contained false --no-restore -o $publish[1]
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($publish[0]) with exit code $LASTEXITCODE." }
    }
    return Find-PublishedDirectories -Source $OutputRoot
}

function Copy-PublishedFiles {
    param(
        [Parameter(Mandatory = $true)][string[]]$Directories,
        [Parameter(Mandatory = $true)][string]$Destination,
        [object[]]$PreviouslyOwnedFiles = @()
    )
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($sourceDirectory in $Directories) {
        foreach ($file in @(Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse)) {
            $relative = Get-RelativeChildPath -Parent $sourceDirectory -Child $file.FullName
            $target = Join-Path $Destination $relative
            $targetParent = Split-Path -Parent $target
            [IO.Directory]::CreateDirectory($targetParent) | Out-Null
            $existed = Test-Path -LiteralPath $target -PathType Leaf
            $oldHash = if ($existed) { Get-FileHashHex -Path $target } else { $null }
            $previous = @($PreviouslyOwnedFiles | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'RelativePath' -and $_.RelativePath -eq $relative }) | Select-Object -First 1
            $wasOwned = $null -ne $previous -and -not [bool]$previous.ExistedBefore
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            $records.Add([pscustomobject]@{
                RelativePath = $relative
                ExistedBefore = $existed -and -not $wasOwned
                PreviousHash = $oldHash
                InstalledHash = Get-FileHashHex -Path $target
            })
        }
    }
    return @($records | Sort-Object RelativePath -Unique)
}

function Write-AtomicText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $temporary = "$Path.tmp-$([guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Add-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($current)) { $parts = @($current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }
    $normalizedPath = ([IO.Path]::GetFullPath($Path)).TrimEnd('\', '/')
    $exists = $false
    foreach ($part in $parts) {
        try { $normalizedPart = ([IO.Path]::GetFullPath($part.Trim())).TrimEnd('\', '/') }
        catch { $normalizedPart = $part.Trim() }
        if ([StringComparer]::OrdinalIgnoreCase.Equals($normalizedPart, $normalizedPath)) { $exists = $true; break }
    }
    if (-not $exists) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $Path) -join ';'), 'User')
    }
    return -not $exists
}

function Invoke-CommandChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & codex @Arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Codex command failed with exit code ${LASTEXITCODE}: codex $($Arguments -join ' ')" }
}

function Register-CodexPlugin {
    param([Parameter(Mandatory = $true)][string]$CodexHomePath)
    if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw 'The codex command is not available; install Codex or pass -SkipPluginRegistration.' }
    [IO.Directory]::CreateDirectory($CodexHomePath) | Out-Null
    $previous = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('CODEX_HOME', $CodexHomePath, 'Process')
        $addOutput = @(& codex plugin marketplace add $marketplaceRoot --json 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Codex marketplace registration failed with exit code $LASTEXITCODE." }
        $pluginId = 'fgo-pet-agent@fgo-pet-local'
        $wasInstalled = $false
        $listOutput = @(& codex plugin list --json 2>&1)
        if ($LASTEXITCODE -eq 0) {
            $listJsonLine = @($listOutput | Where-Object { $_.ToString().TrimStart().StartsWith('{') } | Select-Object -Last 1)
            try {
                $wasInstalled = @((ConvertFrom-Json ($listJsonLine -join '')).installed | Where-Object { $_.pluginId -eq $pluginId }).Count -gt 0
            }
            catch { $wasInstalled = $false }
        }
        $pluginOutput = @(& codex plugin add $pluginId --json 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Codex plugin registration failed with exit code $LASTEXITCODE." }
        $alreadyAdded = $false
        # Codex may print a warning to stderr before the JSON result. Parse the
        # result line only; a warning must never make us claim ownership of an
        # existing marketplace source.
        $addJsonLine = @($addOutput | Where-Object { $_.ToString().TrimStart().StartsWith('{') } | Select-Object -Last 1)
        try { $alreadyAdded = [bool]((ConvertFrom-Json ($addJsonLine -join '')).alreadyAdded) } catch { }
        return [pscustomobject]@{
            MarketplaceAddedByInstaller = -not $alreadyAdded
            PluginRegistered = $true
            PluginAddedByInstaller = -not $wasInstalled
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('CODEX_HOME', $previous, 'Process')
    }
}

function Invoke-McpSmoke {
    param(
        [Parameter(Mandatory = $true)][string]$ShimPath,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$StateRoot
    )
    $suffix = 'smoke-' + [guid]::NewGuid().ToString('N')
    [IO.Directory]::CreateDirectory($StateRoot) | Out-Null
    $comSpec = [Environment]::GetEnvironmentVariable('ComSpec')
    if ([string]::IsNullOrWhiteSpace($comSpec)) { $comSpec = Join-Path $env:WINDIR 'System32\cmd.exe' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $comSpec
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $InstallDirectory
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Use the raw command-line form required by cmd.exe for a quoted /c target.
    # ArgumentList would escape the embedded quotes as backslashes.
    if ($ShimPath.Contains('"')) { throw 'The installed shim path contains an invalid quote.' }
    $info.Arguments = '/d /s /c ""' + $ShimPath + '" mcp"'
    $info.Environment['FGO_PET_PIPE_SUFFIX'] = $suffix
    $info.Environment['FGO_PET_STATE_ROOT'] = $StateRoot
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    try {
        if (-not $process.Start()) { throw 'The installed adapter shim could not be started.' }
        $started = $true
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"fgo-pet-installer-smoke","version":"1"}}}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill($true) } catch { }
            throw 'The installed adapter MCP smoke test timed out after 15 seconds.'
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) { throw "The installed adapter MCP smoke test failed with exit code $($process.ExitCode)." }
        $responses = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            try { ConvertFrom-Json $_ } catch { throw 'The installed adapter MCP smoke test returned invalid JSON.' }
        })
        if ($responses.Count -ne 2 -or @($responses | Where-Object { $_.id -eq 1 }).Count -ne 1 -or
            @($responses | Where-Object { $_.id -eq 2 -and $null -ne $_.result.tools }).Count -ne 1) {
            throw 'The installed adapter MCP smoke test did not return initialize and tools/list responses.'
        }
        Write-Host 'MCP smoke passed: initialize and tools/list.'
    }
    finally {
        if ($started -and -not $process.HasExited) { try { $process.Kill($true) } catch { } }
        $process.Dispose()
        # The adapter may have bootstrapped a Relay. Match both opaque markers
        # before stopping anything; never enumerate or stop unrelated processes.
        try {
            Get-CimInstance Win32_Process -Filter "Name = 'FgoPet.AgentRelay.exe'" -ErrorAction Stop |
                Where-Object { $_.CommandLine -like "*$suffix*" -and $_.CommandLine -like "*$StateRoot*" } |
                ForEach-Object {
                    try { Invoke-CimMethod -InputObject $_ -MethodName Terminate -ErrorAction SilentlyContinue | Out-Null }
                    catch { }
                }
        }
        catch { Write-Warning 'Could not inspect the isolated smoke Relay process; verify no process remains before cleanup.' }
    }
}

$tempPublishRoot = $null
$smokeStateRoot = $null
$installDirectory = $null
try {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Resolve-DefaultInstallRoot }
    $installDirectory = Resolve-SafeDirectory -Value $InstallRoot -Label 'InstallRoot' -Create
    if ([string]::IsNullOrWhiteSpace($CodexHome)) { $CodexHome = Resolve-DefaultCodexHome }
    $codexHomePath = Resolve-SafeDirectory -Value $CodexHome -Label 'CodexHome' -Create
    if ($LegacySkipPathUpdate) { $SkipUserPath = $true }
    if ($LegacySkipPluginInstall) { $SkipPluginRegistration = $true }

    $package = Assert-PluginPackage
    $markerPath = Join-Path $installDirectory $markerName
    $previousMarker = $null
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        try { $previousMarker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json }
        catch { throw "The existing installer marker is corrupt; refusing to overwrite untracked installation state: $markerPath" }
        if ($previousMarker.SchemaVersion -ne 1 -or
            ([IO.Path]::GetFullPath([string]$previousMarker.InstallRoot)).TrimEnd('\', '/') -ne $installDirectory) {
            throw 'The existing installer marker does not belong to the requested InstallRoot.'
        }
    }
    $previousFiles = if ($null -ne $previousMarker) { @($previousMarker.Files) } else { @() }
    if ([string]::IsNullOrWhiteSpace($PublishedSource)) {
        if ($SkipBuild) { throw 'Pass -PublishedSource when -SkipBuild is used.' }
        $tempPublishRoot = Join-Path ([IO.Path]::GetTempPath()) ('fgo-pet-publish-' + [guid]::NewGuid().ToString('N'))
        $published = Publish-ReleaseBinaries -OutputRoot $tempPublishRoot
    }
    else {
        $published = Find-PublishedDirectories -Source $PublishedSource
    }

    $beforeInstall = Test-Path -LiteralPath $installDirectory -PathType Container
    $copied = Copy-PublishedFiles -Directories $published.Directories -Destination $installDirectory -PreviouslyOwnedFiles $previousFiles
    $shimPath = Join-Path $installDirectory $shimName
    $shimText = "@echo off`r`nsetlocal`r`n`"%~dp0FgoPet.CodexAdapter.exe`" %*`r`nexit /b %ERRORLEVEL%`r`n"
    $shimExisted = Test-Path -LiteralPath $shimPath -PathType Leaf
    $shimPreviousHash = if ($shimExisted) { Get-FileHashHex -Path $shimPath } else { $null }
    $previousShim = @($previousFiles | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'RelativePath' -and $_.RelativePath -eq $shimName }) | Select-Object -First 1
    $shimWasOwned = $null -ne $previousShim -and -not [bool]$previousShim.ExistedBefore
    Write-AtomicText -Path $shimPath -Content $shimText
    $copied += [pscustomobject]@{
        RelativePath = $shimName
        ExistedBefore = $shimExisted -and -not $shimWasOwned
        PreviousHash = $shimPreviousHash
        InstalledHash = Get-FileHashHex -Path $shimPath
    }

    $pathAdded = $null -ne $previousMarker -and [bool]$previousMarker.PathEntryAdded
    if (-not $SkipUserPath) {
        $pathAdded = (Add-UserPathEntry -Path $installDirectory) -or $pathAdded
        if ($pathAdded -and -not ($null -ne $previousMarker -and [bool]$previousMarker.PathEntryAdded)) { Write-Host "Added the adapter directory to the user PATH: $installDirectory (restart Codex to reload PATH)." }
        else { Write-Host 'The adapter directory is already present in the user PATH.' }
    }
    else { Write-Host 'Skipped user PATH update.' }

    $registration = [pscustomobject]@{
        MarketplaceAddedByInstaller = $null -ne $previousMarker -and [bool]$previousMarker.MarketplaceAddedByInstaller
        PluginRegistered = $null -ne $previousMarker -and [bool]$previousMarker.PluginRegistered
        PluginAddedByInstaller = $null -ne $previousMarker -and [bool]$previousMarker.PluginAddedByInstaller
    }
    if (-not $SkipPluginRegistration) {
        $currentRegistration = Register-CodexPlugin -CodexHomePath $codexHomePath
        $registration = [pscustomobject]@{
            MarketplaceAddedByInstaller = [bool]$registration.MarketplaceAddedByInstaller -or [bool]$currentRegistration.MarketplaceAddedByInstaller
            PluginRegistered = $true
            PluginAddedByInstaller = [bool]$registration.PluginAddedByInstaller -or [bool]$currentRegistration.PluginAddedByInstaller
        }
        Write-Host 'Codex plugin registration completed.'
    }
    else { Write-Host 'Skipped Codex plugin registration.' }

    $smokeStateRoot = Join-Path ([IO.Path]::GetTempPath()) ('fgo-pet-smoke-state-' + [guid]::NewGuid().ToString('N'))
    Invoke-McpSmoke -ShimPath $shimPath -InstallDirectory $installDirectory -StateRoot $smokeStateRoot

    $marker = [pscustomobject]@{
        SchemaVersion = 1
        InstalledUtc = [DateTime]::UtcNow.ToString('O')
        InstallRoot = $installDirectory
        Files = @($copied)
        PathEntryAdded = $pathAdded
        PluginName = $package.PluginName
        MarketplaceName = $package.MarketplaceName
        MarketplaceSource = $marketplaceRoot
        CodexHome = $codexHomePath
        MarketplaceAddedByInstaller = [bool]$registration.MarketplaceAddedByInstaller
        PluginRegistered = [bool]$registration.PluginRegistered
        PluginAddedByInstaller = [bool]$registration.PluginAddedByInstaller
    }
    $markerPath = Join-Path $installDirectory $markerName
    Write-AtomicText -Path $markerPath -Content ($marker | ConvertTo-Json -Depth 6)
    Write-Host "FGO Pet Codex adapter installed under $installDirectory."
}
finally {
    if ($null -ne $smokeStateRoot -and (Test-Path -LiteralPath $smokeStateRoot)) {
        Remove-Item -LiteralPath $smokeStateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $tempPublishRoot -and (Test-Path -LiteralPath $tempPublishRoot)) {
        Remove-Item -LiteralPath $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
