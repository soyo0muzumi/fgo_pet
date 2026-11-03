[CmdletBinding()]
param(
    [string]$Python = 'python',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/web-search/runtime')
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Build the Windows search runtime on Windows with 64-bit Python 3.11 or 3.12.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'The output directory is not empty. Select a new output directory to preserve the existing runtime.'
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ('fgopet-search-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    & $Python -c "import sys, struct; assert sys.version_info[:2] in ((3,11),(3,12)) and struct.calcsize('P') == 8, 'Use 64-bit Python 3.11 or 3.12'"
    if ($LASTEXITCODE -ne 0) { throw 'Unsupported build Python.' }
    & $Python -m venv (Join-Path $stage 'venv')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the isolated search build environment.' }
    $buildPython = Join-Path $stage 'venv/Scripts/python.exe'
    & $buildPython -m pip install --isolated --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements-build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install the pinned search build dependencies.' }
    & $buildPython -m PyInstaller --noconfirm --clean --onedir --console --name FgoPet.WebSearch `
        --distpath (Join-Path $stage 'dist') --workpath (Join-Path $stage 'build') --specpath $stage `
        --collect-submodules ddgs.engines --copy-metadata ddgs --collect-binaries primp `
        (Join-Path $PSScriptRoot 'search_worker.py')
    if ($LASTEXITCODE -ne 0) { throw 'Search runtime bundling failed.' }
    $bundle = Join-Path $stage 'dist/FgoPet.WebSearch'
    & $buildPython (Join-Path $PSScriptRoot 'package_runtime.py') $bundle
    if ($LASTEXITCODE -ne 0) { throw 'Search runtime manifest or license collection failed.' }
    $check = & (Join-Path $bundle 'FgoPet.WebSearch.exe') --self-test
    if ($LASTEXITCODE -ne 0) { throw 'The bundled search runtime failed its offline self-test.' }
    $health = $check | ConvertFrom-Json
    if (!$health.success -or $health.protocolVersion -ne 1 -or $health.dependencyVersion -ne '9.16.1') {
        throw 'Unexpected search runtime self-test response.'
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Get-ChildItem -LiteralPath $bundle -Force | Copy-Item -Destination $output -Recurse
    Write-Output ('Search runtime ready: ' + $output)
} finally {
    # Only this invocation's unique, newly created temporary build directory is removed.
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
