[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RelativePosixPath([string]$Root, [string]$Path) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\\', '/')
    if ([System.IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('../')) {
        throw "Unsafe payload path."
    }
    return $relative
}

function Test-ForbiddenPayloadPath([string]$RelativePath) {
    $leaf = [System.IO.Path]::GetFileName($RelativePath).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    $forbiddenExtensions = @('.fgopetpack', '.cs', '.csproj', '.sln', '.py', '.ps1', '.pdb', '.log')
    $forbiddenNames = @('credentials.json', 'pairing.json', 'settings.json', 'runtime.sqlite', '.git', '.env')
    return $forbiddenExtensions -contains $extension -or $forbiddenNames -contains $leaf -or
        $RelativePath.ToLowerInvariant().Contains('/screenshots/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
    & dotnet publish $appProject -c Release -p:PublishProfile=win-x64-release -o $payloadRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw "Application publish failed." }

    $files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "Published application payload is empty." }
    $entries = foreach ($file in $files) {
        $relativePath = Get-RelativePosixPath $payloadRoot $file.FullName
        if (Test-ForbiddenPayloadPath $relativePath) { throw "Forbidden payload file: $relativePath" }
        [ordered]@{ path = $relativePath; sha256 = Get-Sha256 $file.FullName; size = $file.Length }
    }

    $requiredExecutables = @('FgoPet.App.exe', 'FgoPet.AgentRelay.exe', 'FgoPet.CodexAdapter.exe')
    $publishedPaths = @($entries | ForEach-Object { $_.path })
    foreach ($required in $requiredExecutables) {
        if ($publishedPaths -notcontains $required) { throw "Required executable is missing from publish output: $required" }
    }

    $projectXml = [xml](Get-Content -LiteralPath $appProject -Raw)
    $version = [string]$projectXml.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.1.0' }
    if ($version -notmatch '^[0-9A-Za-z.-]+$') { throw "Application version contains unsupported characters." }

    $appDirectory = Join-Path $stage 'app'
    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
    $archiveName = "FgoPet-win-x64-$version.zip"
    $archivePath = Join-Path $appDirectory $archiveName
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entry in $entries) {
            $zipEntry = $archive.CreateEntry($entry.path, [System.IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [System.IO.File]::OpenRead((Join-Path $payloadRoot $entry.path.Replace('/', '\\')))
            $output = $zipEntry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }

    $manifest = [ordered]@{
        schema_version = 1
        runtime_identifier = 'win-x64'
        framework_dependent = $true
        application_version = $version
        required_executables = $requiredExecutables
        files = @($entries | Sort-Object path)
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    ('{0}  app/{1}' -f (Get-Sha256 $archivePath), $archiveName) | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS') -Encoding ascii

    Move-Item -LiteralPath $stage -Destination $destination
    Write-Output "Release candidate created: $destination"
}
catch {
    throw "Release publishing failed: $($_.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
