$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$speechRoot = Join-Path $repositoryRoot 'plugins\FgoPet.Plugin.Speech'
$productionProjects = @(
    @{ Name = 'FgoPet.Speech.Core'; Path = 'Contracts\FgoPet.Speech.Core.csproj' }
    @{ Name = 'FgoPet.Plugin.Speech.Core'; Path = 'Core\FgoPet.Plugin.Speech.Core.csproj' }
    @{ Name = 'FgoPet.Speech.Infrastructure'; Path = 'Infrastructure\FgoPet.Speech.Infrastructure.csproj' }
    @{ Name = 'FgoPet.Speech.Desktop'; Path = 'Desktop\FgoPet.Speech.Desktop.csproj' }
)
$unitTestProjects = @(
    @{ Name = 'FgoPet.Speech.Core.Tests'; Path = 'tests\FgoPet.Speech.Core.Tests\FgoPet.Speech.Core.Tests.csproj' }
    @{ Name = 'FgoPet.Speech.Infrastructure.Tests'; Path = 'tests\FgoPet.Speech.Infrastructure.Tests\FgoPet.Speech.Infrastructure.Tests.csproj' }
    @{ Name = 'FgoPet.Speech.Desktop.Tests'; Path = 'tests\FgoPet.Speech.Desktop.Tests\FgoPet.Speech.Desktop.Tests.csproj' }
)

Describe 'Speech module layout' {
    It 'contains all speech production projects under the plugin production roots' {
        foreach ($project in $productionProjects) {
            $projectPath = Join-Path $speechRoot $project.Path
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'contains all speech unit-test projects under the repository test root' {
        foreach ($project in $unitTestProjects) {
            $projectPath = Join-Path $repositoryRoot $project.Path
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'keeps speech implementation and tests out of legacy feature project paths' {
        $legacyPaths = @(
            'src\FgoPet.Core\Speech'
            'src\FgoPet.Infrastructure\Speech'
            'src\FgoPet.App\Speech'
            'src\FgoPet.App\Settings\SpeechConnectionPage.xaml'
            'src\FgoPet.App\Settings\SpeechConnectionPage.xaml.cs'
            'src\FgoPet.App\Settings\SpeechConnectionViewModel.cs'
            'tests\FgoPet.App.Tests\Speech'
            'tests\FgoPet.Infrastructure.Tests\Speech'
        )
        foreach ($relativePath in $legacyPaths) {
            Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath) | Should Be $false
        }
    }

    It 'lists all speech projects in the solution and includes module ownership docs' {
        $solutionText = (Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'FgoPet.sln')) -replace '\\', '/'
        Test-Path -LiteralPath (Join-Path $speechRoot 'README.md') -PathType Leaf | Should Be $true
        Test-Path -LiteralPath (Join-Path $speechRoot 'AGENTS.md') -PathType Leaf | Should Be $true
        foreach ($project in $productionProjects) {
            $solutionPath = "plugins/FgoPet.Plugin.Speech/$($project.Path.Replace('\', '/'))"
            $solutionText | Should Match ([regex]::Escape($solutionPath))
        }
        foreach ($project in $unitTestProjects) {
            $solutionText | Should Match ([regex]::Escape($project.Path.Replace('\', '/')))
        }
    }

    It 'does not reference the desktop application from the speech module' {
        foreach ($project in $productionProjects) {
            $projectPath = Join-Path $speechRoot $project.Path
            if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
                (Get-Content -Raw -LiteralPath $projectPath) | Should Not Match 'src\\FgoPet\.App\\FgoPet\.App\.csproj'
            }
        }
    }
    It 'does not reference the full Infrastructure or Agent implementation from speech production projects' {
        foreach ($project in $productionProjects) {
            $projectPath = Join-Path $speechRoot $project.Path
            $projectText = Get-Content -Raw -LiteralPath $projectPath
            $projectText | Should Not Match 'FgoPet\.Infrastructure\.csproj'
            $projectText | Should Not Match 'plugins\\providers\\FgoPet.Provider.Codex'
        }
    }
}
