param(
    [string]$Archive = ""
)

$ErrorActionPreference = "Stop"
$publishProfilePath = Join-Path $PSScriptRoot "..\src\FgoPet.App\Properties\PublishProfiles\win-x64-release.pubxml"
if (-not (Test-Path -LiteralPath $publishProfilePath -PathType Leaf)) {
    throw "Required publish profile does not exist: $publishProfilePath"
}

[xml]$publishProfile = Get-Content -LiteralPath $publishProfilePath -Raw
$expectedProfileProperties = [ordered]@{
    RuntimeIdentifier = "win-x64"
    SelfContained = "false"
    Configuration = "Release"
    PublishSingleFile = "false"
    IncludeNativeLibrariesForSelfExtract = "false"
}
foreach ($expectedProperty in $expectedProfileProperties.GetEnumerator()) {
    $actualValue = [string]$publishProfile.Project.PropertyGroup.($expectedProperty.Key)
    if ($actualValue -ne $expectedProperty.Value) {
        throw "Publish profile property $($expectedProperty.Key) must be '$($expectedProperty.Value)', but was '$actualValue'."
    }
}
Write-Output "Publish profile contract passed."

$pythonCandidates = @(
    (Join-Path $PSScriptRoot "..\.venv-phase5-4a\Scripts\python.exe"),
    (Join-Path $PSScriptRoot "..\..\.venv-phase5-4a\Scripts\python.exe"),
    (Join-Path $PSScriptRoot "..\..\..\..\.venv-phase5-4a\Scripts\python.exe"),
    "python"
)
$python = $pythonCandidates | Where-Object {
    $_ -eq "python" -or (Test-Path -LiteralPath $_)
} | Select-Object -First 1

& $python -m pytest tests/art tests/packs -q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Packs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~Packs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Archive -ne "") {
    $archivePath = [System.IO.Path]::GetFullPath($Archive)
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Archive does not exist: $Archive"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $allowed = @(".png", ".jpg", ".jpeg", ".json", ".md", ".txt")
        $names = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([System.IO.Path]::IsPathRooted($name) -or $name.Split('/') -contains '..') {
                throw "Unsafe archive member: $name"
            }
            if (-not $entry.FullName.EndsWith('/') -and $allowed -notcontains [System.IO.Path]::GetExtension($name).ToLowerInvariant()) {
                throw "Forbidden archive member: $name"
            }
            if ($names.ContainsKey($name)) {
                throw "Duplicate archive member: $name"
            }
            $names[$name] = $true
        }
        Write-Output ("Archive allowlist passed: {0} members" -f $zip.Entries.Count)
    }
    finally {
        $zip.Dispose()
    }
}

Write-Output "Packaging gate passed."
