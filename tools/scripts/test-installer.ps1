[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Installer
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$path = [System.IO.Path]::GetFullPath($Installer)
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installer not found: $path" }
if ([System.IO.Path]::GetExtension($path).ToLowerInvariant() -ne '.msi') { throw "Installer must be an MSI." }
$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $path).Length
[ordered]@{
    status = 'READY_FOR_MANUAL_LIFECYCLE'
    installer = $path
    sha256 = $hash
    bytes = $size
    install_steps = @(
        'Install the MSI as the current Windows user and launch FgoPet.App.exe.',
        'Choose a non-default install directory and confirm the application launches from it.',
        'Run the MSI again as a separate upgrade test and confirm user data remains.'
    )
    uninstall_steps = @(
        'Open Windows Settings > Apps > Installed apps as a separate uninstall test.',
        'Uninstall FGO Pet and confirm installed program files are removed.',
        'Confirm user data remains after uninstall.'
    )
} | ConvertTo-Json -Depth 4
