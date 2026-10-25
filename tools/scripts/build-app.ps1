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

# Why this script exists
# ----------------------
# FgoPet.App.csproj embeds the Live2D viewer runtime only when the MSBuild property
# FgoPetLive2DRuntime is supplied:
#
#   <ItemGroup Condition="'$(FgoPetLive2DRuntime)' != '' and Exists('$(FgoPetLive2DRuntime)')">
#     <Content Include="$(FgoPetLive2DRuntime)\**\*">
#       <Link>Live2D\%(RecursiveDir)%(Filename)%(Extension)</Link> ...
#
# Building without it is not merely "missing a feature": MSBuild's IncrementalClean
# deletes the previously copied runtime files because they are no longer produced by
# the current build, leaving an EMPTY Live2D/ directory skeleton. Live2DSession then
# fails its file probes and silently falls back to the static portrait, so the symptom
# a user sees is "Live2D runtime is wrong" with no error anywhere.
#
# This script therefore resolves the runtime, refuses to build without one, and
# asserts the result afterwards. Do not "fix" a failing build by dropping this script.
#
# tools/scripts/publish-release.ps1 applies the same rule to the release payload.

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
