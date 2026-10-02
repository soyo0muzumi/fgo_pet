from pathlib import Path
import subprocess

import pytest


SCRIPT = Path(__file__).parents[2] / "tools/scripts/test-phase4.ps1"


@pytest.mark.parametrize("scenario", ["owned", "unrelated", "timeout"])
def test_phase4_waits_only_for_owned_webview_processes(scenario):
    source = str(SCRIPT).replace("'", "''")
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", f"""
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{source}', [ref]$null, [ref]$null)
$definition = $ast.Find({{ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Wait-Phase4WebViewExit'
}}, $true)
if ($null -eq $definition) {{ throw 'Missing bounded WebView exit wait.' }}
. ([scriptblock]::Create($definition.Extent.Text))
$ownedRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'fgo-pet-phase4-test/t03'))
$profileRoot = if ('{scenario}' -eq 'unrelated') {{ $ownedRoot + '-other' }} else {{ $ownedRoot }}
$child = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 2' -PassThru -WindowStyle Hidden
function Get-CimInstance {{
    [pscustomobject]@{{ ProcessId = $child.Id; CommandLine = '--user-data-dir="' + $profileRoot + '\\WebView2"' }}
}}
try {{
    $timer = [Diagnostics.Stopwatch]::StartNew()
    if ('{scenario}' -eq 'timeout') {{
        try {{ Wait-Phase4WebViewExit -Root $ownedRoot -TimeoutSeconds 0; throw 'Timeout was swallowed.' }}
        catch {{ if ($_.Exception.Message -notmatch 'WebView2 process did not exit') {{ throw }} }}
    }} else {{
        Wait-Phase4WebViewExit -Root $ownedRoot -TimeoutSeconds 15
        if ('{scenario}' -eq 'owned' -and -not $child.HasExited) {{ throw 'Owned process was not awaited.' }}
        if ('{scenario}' -eq 'unrelated' -and $timer.Elapsed.TotalSeconds -ge 1) {{ throw 'Unrelated profile was awaited.' }}
    }}
}} finally {{
    if (-not $child.HasExited) {{ $child.Kill(); $child.WaitForExit() }}
    $child.Dispose()
}}
"""],
        capture_output=True, text=True, encoding="utf-8", timeout=25,
    )
    assert result.returncode == 0, result.stdout + result.stderr
