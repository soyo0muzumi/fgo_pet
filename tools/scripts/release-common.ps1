# Shared helpers for the FgoPet build/publish/verify scripts.
#
# Dot-source this file; do not execute it directly:
#     . (Join-Path $PSScriptRoot 'release-common.ps1')
#
# ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less script files as
# ANSI, so non-ASCII text in these scripts would mojibake regardless of the console.
#
# Why these helpers are centralised: the payload gate below used to be copy-pasted
# into both publish-release.ps1 and verify-release.ps1, and the copies drifted into
# the same defect (see Get-FgoPetRelativePosixPath) while silently weakening the
# segment rules. One definition, one place to fix.

$FgoPetLive2DRuntimeFiles = @('index.html', 'app.js', 'live2dcubismcore.js')

# The Live2D viewer runtime ships its third-party notices as Markdown. The payload
# gate forbids .md in general (Markdown is normally repository documentation, which
# must never ship), so allow exactly these two files and nothing else. Licensing
# requires them to travel with the runtime, so they cannot simply be renamed away
# without also rewriting the notice text they reference.
$FgoPetAllowedPayloadMarkdown = @('live2d/core-license.md', 'live2d/framework-license.md')

function Get-FgoPetRelativePosixPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $pathFull = [System.IO.Path]::GetFullPath($Path)

    # Windows PowerShell 5.1 runs on .NET Framework, which has no Path.GetRelativePath.
    $probe = [System.IO.Path].GetMethod('GetRelativePath', [type[]]@([string], [string]))
    if ($null -ne $probe) {
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $pathFull)
    }
    else {
        $prefix = $rootFull.TrimEnd(@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe payload path.'
        }
        $relative = $pathFull.Substring($prefix.Length)
    }

    # Do NOT "simplify" this back to .Replace('\\', '/'): in a PowerShell single-quoted
    # string '\\' is TWO characters, so it never matches the single separators a Windows
    # path contains. That defect shipped in earlier revisions; the manifest then carried
    # backslashes and verify-release.ps1 rejected every path with 'forbidden or unsafe'.
    $relative = $relative.Replace('\', '/')

    if ([System.IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('../')) {
        throw 'Unsafe payload path.'
    }
    return $relative
}

function Test-FgoPetForbiddenPayloadPath {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/').ToLowerInvariant()
    if ($FgoPetAllowedPayloadMarkdown -contains $normalized) { return $false }

    $segments = $normalized.Split('/')
    $leaf = $segments[-1]
    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    $forbiddenExtensions = @('.fgopetpack', '.cs', '.csproj', '.sln', '.py', '.ps1', '.pdb', '.log', '.db', '.db3', '.pfx', '.pem', '.key', '.snk', '.md')
    $forbiddenNames = @('settings.json', '.env')
    $forbiddenDirectories = @('.git', '.vs', '.idea', 'source', 'src', 'tests', 'test', 'docs', 'doc', 'scripts', 'screenshots', 'screenshot', 'logs', 'log')
    $sensitiveNamePatterns = @('*credential*', '*secret*', '*password*', '*token*', '*pairing*', '*user-data*', '*userdata*', '*user_data*')
    if ($forbiddenExtensions -contains $extension -or $forbiddenNames -contains $leaf -or $extension -like '.sqlite*' -or
        @($segments | Where-Object { $forbiddenDirectories -contains $_ }).Count -gt 0) { return $true }
    foreach ($segment in $segments) {
        if (@($sensitiveNamePatterns | Where-Object { $segment -like $_ }).Count -gt 0) { return $true }
    }
    return $false
}

function Test-FgoPetLive2DRuntimeDirectory {
    param([AllowEmptyString()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    foreach ($name in $FgoPetLive2DRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) { return $false }
    }
    return $true
}

function Resolve-FgoPetLive2DRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [AllowEmptyString()][string]$Explicit
    )

    # Explicit intent must never silently fall back: if the caller named a runtime
    # directory (parameter or environment variable) and it is unusable, fail here.
    # Silent fallback is exactly how the empty-Live2D-directory bug stayed invisible.
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $resolved = [System.IO.Path]::GetFullPath($Explicit)
        if (Test-FgoPetLive2DRuntimeDirectory $resolved) { return $resolved }
        throw "-Live2DRuntime does not contain the required files ($($FgoPetLive2DRuntimeFiles -join ', ')): $resolved"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:FGOPET_LIVE2D_RUNTIME)) {
        $fromEnvironment = [System.IO.Path]::GetFullPath($env:FGOPET_LIVE2D_RUNTIME)
        if (Test-FgoPetLive2DRuntimeDirectory $fromEnvironment) { return $fromEnvironment }
        throw "`$env:FGOPET_LIVE2D_RUNTIME does not contain the required files ($($FgoPetLive2DRuntimeFiles -join ', ')): $fromEnvironment"
    }

    # Best-effort discovery: conventional path first, then the newest prepared runtime
    # left under artifacts/.
    $conventional = Join-Path $RepoRoot 'artifacts\live2d-runtime'
    if (Test-FgoPetLive2DRuntimeDirectory $conventional) { return $conventional }

    $artifactsRoot = Join-Path $RepoRoot 'artifacts'
    if (Test-Path -LiteralPath $artifactsRoot -PathType Container) {
        $found = @(
            Get-ChildItem -LiteralPath $artifactsRoot -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'live2d' } |
                Sort-Object LastWriteTime -Descending |
                ForEach-Object { $_.FullName }
        )
        foreach ($candidate in $found) {
            if (Test-FgoPetLive2DRuntimeDirectory $candidate) { return $candidate }
        }
    }

    throw @"
Live2D viewer runtime not found, refusing to continue.

Expected a directory containing: $($FgoPetLive2DRuntimeFiles -join ', ')

Provide one of:
  - -Live2DRuntime <dir>
  - `$env:FGOPET_LIVE2D_RUNTIME = '<dir>'
  - put the prepared runtime at artifacts/live2d-runtime

Generate it with (needs the Cubism SDK for Web and a local esbuild):
  python tools/scripts/prepare_live2d_runtime.py --sdk <CubismSdkForWeb> --esbuild <esbuild> --output artifacts/live2d-runtime
"@
}
