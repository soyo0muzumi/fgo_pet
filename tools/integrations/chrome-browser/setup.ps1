[CmdletBinding()]
param(
    [string]$RuntimeDirectory = (Join-Path $PSScriptRoot '../../../artifacts/chrome-browser/runtime'),
    [string]$StateRoot = $(if ($env:FGO_PET_STATE_ROOT) { $env:FGO_PET_STATE_ROOT } else { Join-Path $env:LOCALAPPDATA 'FgoPet' }),
    [string]$UserDataDirectory,
    [switch]$Enable
)
$ErrorActionPreference = 'Stop'
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
$stateDirectory = [IO.Path]::GetFullPath($StateRoot)
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
$npmPath = (Get-Command npm.cmd -ErrorAction Stop).Source
$settingsPath = Join-Path $stateDirectory 'chrome-browser.json'
if ($UserDataDirectory -and (!(Test-Path -LiteralPath $UserDataDirectory -PathType Container) -or ![IO.Path]::IsPathFullyQualified($UserDataDirectory))) {
    throw 'An explicit Chrome profile directory must be an existing absolute path.'
}
if ($Enable -and (Test-Path -LiteralPath $settingsPath)) {
    throw 'Chrome settings already exist. Review that file before replacing an existing grant.'
}
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
# Pinned dependency, local to this integration. No lifecycle scripts, browser download, or global installation.
& $npmPath install --prefix $runtimeRoot --save-exact --ignore-scripts --omit=optional --no-audit --no-fund 'chrome-devtools-mcp@1.10.1'
if ($LASTEXITCODE -ne 0) { throw 'Chrome MCP dependency installation failed.' }
$serverPath = Join-Path $runtimeRoot 'node_modules/chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js'
if (!(Test-Path -LiteralPath $serverPath)) { throw 'Chrome MCP entry point is missing.' }
if ($Enable) {
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $grant = @{ Enabled = $true; NodePath = $nodePath; ServerPath = $serverPath }
    if ($UserDataDirectory) { $grant.UserDataDirectory = $UserDataDirectory }
    $settings = $grant | ConvertTo-Json
    $stream = [IO.File]::Open($settingsPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($settings)
        $stream.Write($bytes)
    } finally { $stream.Dispose() }
    Write-Output 'Chrome browsing enabled for the active role and its current conversations. Chrome still requires its own connection approval.'
} else {
    Write-Output 'Chrome MCP installed locally. FGO Pet access remains disabled until an explicit host grant is configured.'
}
