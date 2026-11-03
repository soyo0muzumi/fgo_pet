[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    # Live2D viewer runtime directory. Falls back to $env:FGOPET_LIVE2D_RUNTIME,
    # then to artifacts/live2d-runtime, then to the newest prepared runtime found
    # under artifacts/. The directory must contain index.html + app.js + live2dcubismcore.js.
    [string]$Live2DRuntime,

    # Skip the post-build assertion that Live2D/ landed in the output.
    [switch]$NoVerify,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Direct dotnet/IDE builds now use src/FgoPet.App/Live2D.targets to include the
# conventional runtime and reject incomplete inputs. This wrapper also supports
# legacy runtime discovery and verifies the resulting application directory.

. (Join-Path $PSScriptRoot 'release-common.ps1')

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$appProject = Join-Path $repoRoot 'src\FgoPet.App\FgoPet.App.csproj'

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "Application project not found: $appProject"
}

$resolvedRuntime = Resolve-FgoPetLive2DRuntime -RepoRoot $repoRoot -Explicit $Live2DRuntime
Write-Output "Live2D runtime: $resolvedRuntime"

$buildArguments = @('build', $appProject, '-c', $Configuration, "-p:FgoPetLive2DRuntime=$resolvedRuntime", '--nologo')
if ($null -ne $DotnetArgs) { $buildArguments += $DotnetArgs }
Write-Output "dotnet $($buildArguments -join ' ')"
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

if ($NoVerify) {
    Write-Output 'Skipped output verification (-NoVerify).'
    return
}

$projectXml = [xml](Get-Content -LiteralPath $appProject -Raw)
$targetFramework = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText
if ([string]::IsNullOrWhiteSpace($targetFramework)) { throw 'Unable to read TargetFramework from the application project.' }

$outputLive2D = Join-Path $repoRoot (Join-Path "src\FgoPet.App\bin\$Configuration\$targetFramework" 'Live2D')
if (-not (Test-FgoPetLive2DRuntimeDirectory $outputLive2D)) {
    throw "Live2D runtime is incomplete in the build output: $outputLive2D"
}
Write-Output "Verified Live2D runtime in output: $outputLive2D"
Write-Output 'Build finished.'
