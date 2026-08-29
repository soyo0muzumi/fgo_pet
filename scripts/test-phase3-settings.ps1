# FgoPet Phase 3 settings verification gate.
# Runs the four project Release test suites serially, then verifies the Release
# publish artifacts. The manual matrix lives in docs/testing/phase3-settings-matrix.md
# (settings paths changed from the Phase 2 matrix).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '== Core tests ==' -ForegroundColor Cyan
    dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

    Write-Host '== Infrastructure tests ==' -ForegroundColor Cyan
    dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure tests failed.' }

    Write-Host '== App tests ==' -ForegroundColor Cyan
    dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'App tests failed.' }

    Write-Host '== Windows tests ==' -ForegroundColor Cyan
    dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }

    Write-Host '== Manual matrix ==' -ForegroundColor Cyan
    Write-Host 'Fill docs/testing/phase3-settings-matrix.md before release; do not mark'
    Write-Host 'Phase 3 releasable while a required cell is unobserved.'
}
finally {
    Pop-Location
}
