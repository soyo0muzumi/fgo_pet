$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$speechRoot = Join-Path $repositoryRoot 'modules\speech'
$productionProjects = @(
    'FgoPet.Speech.Core'
    'FgoPet.Speech.Infrastructure'
    'FgoPet.Speech.Desktop'
)
$unitTestProjects = @(
    'FgoPet.Speech.Core.Tests'
    'FgoPet.Speech.Infrastructure.Tests'
    'FgoPet.Speech.Desktop.Tests'
)

Describe 'Speech module layout' {
    It 'contains all speech production projects under the module source root' {
        foreach ($projectName in $productionProjects) {
            $projectPath = Join-Path $speechRoot "src\$projectName\$projectName.csproj"
            Test-Path -LiteralPath $projectPath -PathType Leaf | Should Be $true
        }
    }

    It 'contains all speech unit-test projects under the module test root' {
        foreach ($projectName in $unitTestProjects) {
            $projectPath = Join-Path $speechRoot "tests\$projectName\$projectName.csproj"
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
        foreach ($projectName in $productionProjects) {
            $solutionText | Should Match ([regex]::Escape("modules/speech/src/$projectName/$projectName.csproj"))
        }
        foreach ($projectName in $unitTestProjects) {
            $solutionText | Should Match ([regex]::Escape("modules/speech/tests/$projectName/$projectName.csproj"))
        }
    }

    It 'does not reference the desktop application from the speech module' {
        foreach ($projectName in $productionProjects) {
            $projectPath = Join-Path $speechRoot "src\$projectName\$projectName.csproj"
            if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
                (Get-Content -Raw -LiteralPath $projectPath) | Should Not Match 'src\\FgoPet\.App\\FgoPet\.App\.csproj'
            }
        }
    }
    It 'does not reference the full Infrastructure or Agent implementation from speech production projects' {
        foreach ($projectName in $productionProjects) {
            $projectPath = Join-Path $speechRoot "src\$projectName\$projectName.csproj"
            $projectText = Get-Content -Raw -LiteralPath $projectPath
            $projectText | Should Not Match 'FgoPet\.Infrastructure\.csproj'
            $projectText | Should Not Match 'modules\\agent-integration'
        }
    }
}