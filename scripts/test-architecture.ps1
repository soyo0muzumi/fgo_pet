[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repositoryRoot
try {
    # The PE reference check must inspect a fresh Release App assembly.
    dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Architecture prerequisite build failed.' }
    dotnet test tests/FgoPet.Architecture.Tests/FgoPet.Architecture.Tests.csproj -c Release --logger trx
    if ($LASTEXITCODE -ne 0) { throw 'Architecture gate failed.' }
}
finally { Pop-Location }
