$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentIntegrationRoot = Join-Path $repositoryRoot 'plugins\providers\FgoPet.Provider.Codex'

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
    'plugins\providers\FgoPet.Provider.Codex'
    'plugins\FgoPet.Plugin.Content\Desktop'
    'plugins\FgoPet.Plugin.Dialogue'
    'plugins\FgoPet.Plugin.Focus'
    'plugins\FgoPet.Plugin.Memory'
    'plugins\FgoPet.Plugin.Speech'
    'plugins\FgoPet.Plugin.Todo'
    'src\FgoPet.Desktop'
    'src\FgoPet.Platform'
    'src\FgoPet.UiSdk\Resources'
    'tests\FgoPet.EndToEnd.Tests'
)

$legacyProjectNames = $productionProjects + ($unitTestProjects | ForEach-Object { $_ -replace '\.Tests$' })
$solutionText = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'FgoPet.sln')
$normalizedSolutionText = $solutionText -replace '\\', '/'

Describe 'Agent integration module layout' {
    It 'contains all four Agent production projects under the module source root' {
        foreach ($projectName in $productionProjects) {
            $projectPath = Join-Path $agentIntegrationRoot "$projectName\$projectName.csproj"
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'contains all four Agent unit-test projects under the module test root' {
        foreach ($projectName in $unitTestProjects) {
            $projectPath = Join-Path $repositoryRoot "tests\$projectName\$projectName.csproj"
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
            $newPath = "plugins/providers/FgoPet.Provider.Codex/$projectName/$projectName.csproj"
            $oldPath = "src/$projectName/$projectName.csproj"
            $normalizedSolutionText | Should Match ([regex]::Escape('"' + $newPath + '"'))
            $normalizedSolutionText | Should Not Match ([regex]::Escape('"' + $oldPath + '"'))
        }

        foreach ($projectName in $unitTestProjects) {
            $newPath = "tests/$projectName/$projectName.csproj"
            $legacyProjectName = $projectName -replace '\.Tests$', ''
            $oldPath = "tests/$legacyProjectName/$legacyProjectName.csproj"
            $normalizedSolutionText | Should Match ([regex]::Escape('"' + $newPath + '"'))
            $normalizedSolutionText | Should Not Match ([regex]::Escape('"' + $oldPath + '"'))
        }
    }
}
