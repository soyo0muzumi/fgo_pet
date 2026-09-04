[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Get-Command wix.exe -ErrorAction SilentlyContinue)) {
    throw "WiX CLI is required. Install the pinned tool with: dotnet tool install --global wix --version 5.0.2"
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = [System.IO.Path]::GetFullPath($ReleaseRoot)
$archive = Get-ChildItem -LiteralPath (Join-Path $release 'app') -Filter 'FgoPet-win-x64-*.zip' -File | Select-Object -First 1
if ($null -eq $archive) { throw "Application ZIP not found under $release\app." }
$version = [regex]::Match($archive.BaseName, 'FgoPet-win-x64-(.+)$').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw "Unable to derive application version from $($archive.Name)." }

$installerProject = Join-Path $root 'installer\FgoPet.Installer\FgoPet.Installer.wixproj'
$packageSource = Join-Path $root 'installer\FgoPet.Installer\Package.wxs'
$license = Join-Path $root 'installer\FgoPet.Installer\license.rtf'
$generated = Join-Path $root 'installer\FgoPet.Installer\HarvestedFiles.wxs'
$work = Join-Path $env:TEMP ('.fgopet-installer-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$output = Join-Path $release 'installer'
New-Item -ItemType Directory -Path $payload, $output -Force | Out-Null
try {
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $payload
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
    $fileIndex = 0
    $fileNodes = $payloadFiles | ForEach-Object {
        $fileIndex++
        $relative = [System.IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/')
        if ($relative.Contains('/')) { throw "Published payload must be flat for the per-user installer: $relative" }
        $source = [System.Security.SecurityElement]::Escape("`$(var.SourceDir)\$relative")
        $id = $_.Name -replace '[^A-Za-z0-9_]', '_'
        $md5 = [System.Security.Cryptography.MD5]::Create()
        $guid = [guid]::new($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("FgoPet/$relative")))
        $md5.Dispose()
        $removeFolder = if ($fileIndex -eq 1) { '<RemoveFolder Id="RemoveInstallFolder" On="uninstall" />' } else { '' }
        "      <Component Id=`"cmp_$id`" Guid=`"$guid`"><File Source=`"$source`" />$removeFolder<RegistryValue Root=`"HKCU`" Key=`"Software\FgoPet\Components\$id`" Name=`"Installed`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /></Component>"
    }
    @(
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
        '  <Fragment>'
        '    <DirectoryRef Id="INSTALLFOLDER">'
        $fileNodes
        '    </DirectoryRef>'
        '    <ComponentGroup Id="HarvestedFiles">'
        ($payloadFiles | ForEach-Object {
            $id = $_.Name -replace '[^A-Za-z0-9_]', '_'
            "      <ComponentRef Id=`"cmp_$id`" />"
        })
        '    </ComponentGroup>'
        '  </Fragment>'
        '</Wix>'
    ) | Set-Content -LiteralPath $generated -Encoding utf8NoBOM

    $obj = Join-Path $work 'FgoPet.Installer.wixobj'
    $msi = Join-Path $output "FgoPet-$version-win-x64.msi"
    & wix build $packageSource $generated -arch x64 -ext WixToolset.UI.wixext -d ProductVersion=$version -d SourceDir=$payload -d WixUILicenseRtf=$license -o $msi
    if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }
    (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + (Split-Path $msi -Leaf) |
        Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding ascii
    Write-Output "Installer created: $msi"
}
finally {
    if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated -Force }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
