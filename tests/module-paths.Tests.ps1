$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$projectPaths = [ordered]@{
    Adapter = 'modules\agent-integration\src\FgoPet.CodexAdapter\FgoPet.CodexAdapter.csproj'
    Relay = 'modules\agent-integration\src\FgoPet.AgentRelay\FgoPet.AgentRelay.csproj'
}

Describe 'Agent integration script project paths' {
    It 'resolves both module-owned projects from the repository root' {
        foreach ($relativePath in $projectPaths.Values) {
            $resolvedPath = Join-Path $repositoryRoot $relativePath

            Test-Path -LiteralPath $resolvedPath -PathType Leaf | Should Be $true
        }
    }

    It 'uses the module-owned project paths in the installer and Phase 4 scripts' {
        $scriptPaths = @(
            'scripts\install-codex-adapter.ps1'
            'scripts\test-phase4.ps1'
        )

        foreach ($scriptPath in $scriptPaths) {
            $scriptContent = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $scriptPath)

            foreach ($relativePath in $projectPaths.Values) {
                $expectedLiteral = "Join-Path `$repositoryRoot '$relativePath'"
                $legacyPath = $relativePath -replace '^modules\\agent-integration\\', ''
                $legacyLiteral = "Join-Path `$repositoryRoot '$legacyPath'"

                $scriptContent | Should Match ([regex]::Escape($expectedLiteral))
                $scriptContent | Should Not Match ([regex]::Escape($legacyLiteral))
            }
        }
    }
}
