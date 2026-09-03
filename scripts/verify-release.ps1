[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Fail([string]$Message) { throw "Release verification failed: $Message" }
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Test-ForbiddenPayloadPath([string]$RelativePath) {
    $leaf = [System.IO.Path]::GetFileName($RelativePath).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    return @('.fgopetpack', '.cs', '.csproj', '.sln', '.py', '.ps1', '.pdb', '.log') -contains $extension -or
        @('credentials.json', 'pairing.json', 'settings.json', 'runtime.sqlite', '.git', '.env') -contains $leaf -or
        $RelativePath.ToLowerInvariant().Contains('/screenshots/')
}
function Test-SafePosixPath([string]$Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and $Path -notmatch '\\' -and $Path -notmatch '^[A-Za-z]:' -and
        -not $Path.StartsWith('/') -and $Path.Split('/') -notcontains '..' -and -not $Path.Split('/') -contains ''
}

try {
    $root = [System.IO.Path]::GetFullPath($CandidateRoot)
    $manifestPath = Join-Path $root 'manifest.json'
    $sumsPath = Join-Path $root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Fail 'manifest.json is missing.' }
    if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) { Fail 'SHA256SUMS is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema_version -ne 1 -or $manifest.runtime_identifier -ne 'win-x64' -or $manifest.framework_dependent -ne $true) { Fail 'manifest runtime contract is invalid.' }
    if (@($manifest.files).Count -eq 0) { Fail 'manifest file list is empty.' }
    $archiveMatches = @(Get-ChildItem -LiteralPath (Join-Path $root 'app') -Filter 'FgoPet-win-x64-*.zip' -File)
    if ($archiveMatches.Count -ne 1) { Fail 'exactly one application archive is required.' }
    $archive = $archiveMatches[0]
    $sumLines = @(Get-Content -LiteralPath $sumsPath | Where-Object { $_ -ne '' })
    if ($sumLines.Count -ne 1 -or $sumLines[0] -notmatch '^([0-9a-f]{64})  app/(FgoPet-win-x64-[0-9A-Za-z.-]+\.zip)$') { Fail 'SHA256SUMS is invalid.' }
    if ($matches[2] -ne $archive.Name -or $matches[1] -ne (Get-Sha256 $archive.FullName)) { Fail 'application archive hash does not match SHA256SUMS.' }

    $expected = @{}
    foreach ($entry in @($manifest.files)) {
        $path = [string]$entry.path
        $hash = [string]$entry.sha256
        if (-not (Test-SafePosixPath $path) -or (Test-ForbiddenPayloadPath $path)) { Fail 'manifest contains a forbidden or unsafe payload path.' }
        if ($hash -notmatch '^[0-9a-f]{64}$') { Fail 'manifest contains a missing or invalid SHA-256 hash.' }
        if ($expected.ContainsKey($path)) { Fail 'manifest contains a duplicate payload path.' }
        $expected[$path] = $hash
    }
    foreach ($required in @($manifest.required_executables)) {
        if ($expected.ContainsKey([string]$required) -eq $false) { Fail 'manifest is missing a required executable.' }
    }
    $requiredSet = @('FgoPet.App.exe', 'FgoPet.AgentRelay.exe', 'FgoPet.CodexAdapter.exe')
    $declaredRequired = (@($manifest.required_executables) | Sort-Object) -join '|'
    $expectedRequired = ($requiredSet | Sort-Object) -join '|'
    if ($declaredRequired -ne $expectedRequired) { Fail 'required executable contract is invalid.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $actual = @{}
        foreach ($member in $zip.Entries) {
            if ($member.FullName.EndsWith('/')) { continue }
            $path = $member.FullName
            if (-not (Test-SafePosixPath $path) -or (Test-ForbiddenPayloadPath $path)) { Fail 'application archive contains a forbidden or unsafe payload path.' }
            if ($actual.ContainsKey($path)) { Fail 'application archive contains a duplicate payload path.' }
            $stream = $member.Open()
            try { $sha = [System.Security.Cryptography.SHA256]::Create(); try { $hash = ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() } } finally { $stream.Dispose() }
            $actual[$path] = $hash
        }
        if ($actual.Count -ne $expected.Count) { Fail 'archive members do not match the manifest.' }
        foreach ($path in $expected.Keys) {
            if (-not $actual.ContainsKey($path) -or $actual[$path] -ne $expected[$path]) { Fail 'archive payload hash does not match the manifest.' }
        }
    }
    finally { $zip.Dispose() }
    Write-Output 'Release verification passed.'
}
catch {
    [Console]::Error.WriteLine("Release verification failed safely.")
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
