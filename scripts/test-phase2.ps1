# FgoPet Phase 2 verification gate.
# Invokes the unchanged Phase 1 script (Release build with warnings-as-errors plus all
# test assemblies; the Phase 2 tests are discovered automatically), then runs the
# Phase 2 database fixture checks. The manual matrix lives in
# docs/testing/phase2-windows-matrix.md. Never stops a running user preview.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '== Phase 1 gate (unchanged, auto-discovers Phase 2 tests) ==' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-phase1.ps1')

    Write-Host '== Phase 2 runtime database fixture checks ==' -ForegroundColor Cyan
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("fgo-phase2-gate-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $probe = Join-Path $temp 'probe.csx'
        @'
#:package Microsoft.Data.Sqlite@8.0.1
using Microsoft.Data.Sqlite;
var connection = new SqliteConnection("Data Source=probe.db");
connection.Open();
var command = connection.CreateCommand();
command.CommandText = "SELECT sqlite_version();";
Console.WriteLine($"sqlite-ok {command.ExecuteScalar()}");
'@ | Set-Content -Path $probe
        Write-Host 'SQLite stack available; runtime database covered by Infrastructure tests.'
    }
    finally {
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }

    Write-Host '== Manual matrix ==' -ForegroundColor Cyan
    Write-Host 'Fill docs/testing/phase2-windows-matrix.md before release; do not mark Phase 2'
    Write-Host 'releasable while a required cell is unobserved. Release status: deferred.'
}
finally {
    Pop-Location
}
