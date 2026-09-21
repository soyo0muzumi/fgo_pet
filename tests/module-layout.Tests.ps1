$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentIntegrationRoot = Join-Path $repositoryRoot 'modules\agent-integration'

$productionProjects = @(
    'FgoPet.AgentProtocol'
    'FgoPet.AgentRelay'
    'FgoPet.AgentRuntime'
    'FgoPet.CodexAdapter'
)

$unitTestProjects = @(
    'FgoPet.AgentProtocol.Tests'
    'FgoPet.AgentRelay.Tests'
    'FgoPet.AgentRuntime.Tests'
    'FgoPet.CodexAdapter.Tests'
)

$approvedRoots = @(
    'modules\agent-integration'
    'modules\character'
    'modules\dialogue'
    'modules\focus'
    'modules\memory'
    'modules\speech'
    'modules\work'
    'host'
    'platform'
    'ui-foundation'
    'integration-tests'
)

$legacyProjectNames = $productionProjects + ($unitTestProjects | ForEach-Object { $_ -replace '\.Tests$' })
$solutionText = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'FgoPet.sln')
$normalizedSolutionText = $solutionText -replace '\\', '/'

Describe 'Agent integration module layout' {
    It 'contains all four Agent production projects under the module source root' {
        foreach ($projectName in $productionProjects) {
            $projectPath = Join-Path $agentIntegrationRoot "src\$projectName\$projectName.csproj"
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'contains all four Agent unit-test projects under the module test root' {
        foreach ($projectName in $unitTestProjects) {
            $projectPath = Join-Path $agentIntegrationRoot "tests\$projectName\$projectName.csproj"
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'contains no legacy Agent project files or source outside ignored build directories' {
        $legacyFiles = @()
        foreach ($legacyRoot in @('src', 'tests')) {
            foreach ($projectName in $legacyProjectNames) {
                $legacyProjectRoot = Join-Path $repositoryRoot "$legacyRoot\$projectName"
                if (Test-Path -LiteralPath $legacyProjectRoot -PathType Container) {
                    $legacyFiles += Get-ChildItem -LiteralPath $legacyProjectRoot -Recurse -File |
                        Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' }
                }
            }
        }

        @($legacyFiles).Count | Should Be 0
    }

    It 'provides README.md and AGENTS.md at every approved module or support root' {
        foreach ($relativeRoot in $approvedRoots) {
            $root = Join-Path $repositoryRoot $relativeRoot
            Test-Path -LiteralPath (Join-Path $root 'README.md') -PathType Leaf | Should Be $true
            Test-Path -LiteralPath (Join-Path $root 'AGENTS.md') -PathType Leaf | Should Be $true
        }
    }

    It 'contains only new Agent project paths in the solution' {
        foreach ($projectName in $productionProjects) {
            $newPath = "modules/agent-integration/src/$projectName/$projectName.csproj"
            $oldPath = "src/$projectName/$projectName.csproj"
            $normalizedSolutionText | Should Match ([regex]::Escape('"' + $newPath + '"'))
            $normalizedSolutionText | Should Not Match ([regex]::Escape('"' + $oldPath + '"'))
        }

        foreach ($projectName in $unitTestProjects) {
            $newPath = "modules/agent-integration/tests/$projectName/$projectName.csproj"
            $oldPath = "tests/$projectName/$projectName.csproj"
            $normalizedSolutionText | Should Match ([regex]::Escape('"' + $newPath + '"'))
            $normalizedSolutionText | Should Not Match ([regex]::Escape('"' + $oldPath + '"'))
        }
    }
}
