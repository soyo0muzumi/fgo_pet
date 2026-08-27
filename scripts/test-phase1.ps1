# FgoPet Phase 1 verification gate.
# Builds Release with warnings-as-errors, runs every non-environmental test, then runs
# the Windows integration tests when an interactive desktop is available. The manual
# matrix lives in docs/testing/phase1-windows-matrix.md.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '== Phase 1 Release build (warnaserror) ==' -ForegroundColor Cyan
    dotnet build FgoPet.sln -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    Write-Host '== Unit / STA tests ==' -ForegroundColor Cyan
    dotnet test FgoPet.sln -c Release --no-build --filter 'Category!=WindowsIntegration'
    if ($LASTEXITCODE -ne 0) { throw 'Unit/STA tests failed.' }

    Write-Host '== Windows integration tests ==' -ForegroundColor Cyan
    dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-build --filter 'Category=WindowsIntegration'
    if ($LASTEXITCODE -ne 0) { throw 'Windows integration tests failed.' }

    Write-Host '== No SkiaSharp in production references ==' -ForegroundColor Cyan
    $skia = dotnet list FgoPet.sln package 2>$null | Select-String -SimpleMatch 'SkiaSharp'
    if ($skia) { throw 'Production projects reference SkiaSharp.' }
    Write-Host 'OK: no SkiaSharp references.'

    Write-Host '== Manual matrix ==' -ForegroundColor Cyan
    Write-Host 'Fill docs/testing/phase1-windows-matrix.md before release; do not mark Phase 1'
    Write-Host 'releasable while a required cell is unobserved.'
}
finally {
    Pop-Location
}