[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot,

    # Live2D viewer runtime directory; resolved by tools/scripts/release-common.ps1.
    [string]$Live2DRuntime
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Payload gate + Live2D runtime resolution live in release-common.ps1 so that publish
# and verify cannot drift apart; they previously carried divergent copies, and both
# copies shared the same path-separator defect (see Get-FgoPetRelativePosixPath).
. (Join-Path $PSScriptRoot 'release-common.ps1')

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$destination = [System.IO.Path]::GetFullPath($OutputRoot)
if ($destination -eq $repoRoot -or $destination.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be outside the repository."
}
if (Test-Path -LiteralPath $destination) {
    throw "OutputRoot must not already exist."
}

$parent = Split-Path -Parent $destination
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw "OutputRoot parent does not exist."
}

$stage = Join-Path $parent ('.fgopet-release-' + [guid]::NewGuid().ToString('N'))
try {
    $payloadRoot = Join-Path $stage 'payload'
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    $appProject = Join-Path $repoRoot 'src\FgoPet.App\FgoPet.App.csproj'
    $resolvedRuntime = Resolve-FgoPetLive2DRuntime -RepoRoot $repoRoot -Explicit $Live2DRuntime
    Write-Output "Live2D runtime: $resolvedRuntime"
    & dotnet publish $appProject -c Release -p:PublishProfile=win-x64-release -p:FgoPetLive2DRuntime=$resolvedRuntime -o $payloadRoot --nologo --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Application publish failed." }

    # A release must never ship without the viewer runtime: an empty Live2D/ directory
    # makes the app fall back to the static portrait with no error anywhere.
    if (-not (Test-FgoPetLive2DRuntimeDirectory (Join-Path $payloadRoot 'Live2D'))) {
        throw "Published payload is missing a complete Live2D/ runtime under $payloadRoot."
    }

    # Project-reference publish output can include PDBs despite the profile;
    # development symbols must never enter the release payload.
    Get-ChildItem -LiteralPath $payloadRoot -Filter '*.pdb' -File -Recurse |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    $files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "Published application payload is empty." }
    $entries = foreach ($file in $files) {
        $relativePath = Get-FgoPetRelativePosixPath -Root $payloadRoot -Path $file.FullName
        if (Test-FgoPetForbiddenPayloadPath -RelativePath $relativePath) { throw "Forbidden payload file: $relativePath" }
        [ordered]@{ path = $relativePath; sha256 = Get-Sha256 $file.FullName; size = $file.Length }
    }

    $requiredExecutables = @('FgoPet.App.exe', 'FgoPet.AgentRelay.exe', 'FgoPet.CodexAdapter.exe')
    $publishedPaths = @($entries | ForEach-Object { $_.path })
    foreach ($required in $requiredExecutables) {
        if ($publishedPaths -notcontains $required) { throw "Required executable is missing from publish output: $required" }
    }

    $projectXml = [xml](Get-Content -LiteralPath $appProject -Raw)
    $targetFramework = [string]$projectXml.Project.PropertyGroup.TargetFramework
    if ($targetFramework -ne 'net8.0-windows10.0.17763.0') { throw "Application target framework must be net8.0-windows10.0.17763.0." }
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    $version = if ($null -eq $versionNode) { '0.1.0' } else { [string]$versionNode.InnerText }
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.1.0' }
    if ($version -notmatch '^[0-9A-Za-z.-]+$') { throw "Application version contains unsupported characters." }

    $appDirectory = Join-Path $stage 'app'
    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
    $archiveName = "FgoPet-win-x64-$version.zip"
    $archivePath = Join-Path $appDirectory $archiveName
    # ZipArchive lives in System.IO.Compression, ZipFile in System.IO.Compression.FileSystem.
    # Loading only the FileSystem assembly leaves [System.IO.Compression.ZipArchive] unresolvable
    # on Windows PowerShell 5.1 (verified on 5.1.26100.9444): 'TypeNotFound'. PowerShell 7 resolves
    # it from the default reference set, which is why this only broke on the 5.1 machines.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # The stream is opened and the archive is constructed INSIDE the try on purpose. Opening the
    # stream first and constructing the archive outside meant a failure between the two leaked the
    # open file handle, leaving a zero-byte, undeletable archive behind and replacing the real error
    # with an IO error from the cleanup block.
    $stream = $null
    $archive = $null
    try {
        $stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        foreach ($entry in $entries) {
            $zipEntry = $archive.CreateEntry($entry.path, [System.IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            # entry.path is POSIX. Use the platform separator directly: a single-quoted '\\' in
            # PowerShell is TWO characters, so .Replace('/', '\\') doubled every separator.
            $input = [System.IO.File]::OpenRead((Join-Path $payloadRoot $entry.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            $output = $zipEntry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally {
        # Dispose the archive first: it owns the stream (leaveOpen = false).
        if ($null -ne $archive) { try { $archive.Dispose() } catch { Write-Warning "Closing the archive failed: $($_.Exception.Message)" } }
        elseif ($null -ne $stream) { try { $stream.Dispose() } catch { Write-Warning "Closing the archive stream failed: $($_.Exception.Message)" } }
    }

    $manifest = [ordered]@{
        schema_version = 1
        runtime_identifier = 'win-x64'
        framework_dependent = $true
        target_framework = $targetFramework
        runtime_requirement = '.NET 8 Desktop Runtime'
        application_version = $version
        required_executables = $requiredExecutables
        files = @($entries | Sort-Object path)
    }
    # 'utf8NoBOM' is a PowerShell 6+ encoding name. Write the bytes directly so this
    # script also runs on Windows PowerShell 5.1 (which the release docs' pwsh does not
    # cover, but which is all some machines have).
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $manifestJson, (New-Object System.Text.UTF8Encoding($false)))
    ('{0}  app/{1}' -f (Get-Sha256 $archivePath), $archiveName) | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS') -Encoding ascii

    Move-Item -LiteralPath $stage -Destination $destination
    Write-Output "Release candidate created: $destination"
}
catch {
    throw "Release publishing failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}
finally {
    # Cleanup must never mask the real failure. The previous revision let a failing Remove-Item
    # (e.g. a still-locked archive) replace the original exception, so the actual cause of a failed
    # release was invisible in the output.
    if (Test-Path -LiteralPath $stage) {
        try { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove staging directory '$stage': $($_.Exception.Message)" }
    }
}
