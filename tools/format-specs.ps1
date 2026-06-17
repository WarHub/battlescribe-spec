<#
.SYNOPSIS
    Auto-formats spec YAML files to match SpecLintTests conventions.
.DESCRIPTION
    Fixes auto-fixable formatting issues in all spec YAML files under specs/ (roster + gamedata).
    Delegates to the `bs-spec format` command (SpecFormatter in BattleScribeSpec.TestKit).

    Formatting rules applied:
    - Removes trailing whitespace
    - Removes redundant 'engines: {}' and 'hidden: false' declarations
    - Reorders expectedState properties: errors/errorsContain → ... → forces → engines
    - Ensures blank line before setup: block
    - Ensures blank lines between step items (action/expectedState)
    - Ensures file ends with a single newline

    The formatter is idempotent: running it twice produces the same result as running it once.
.PARAMETER Check
    Report issues without fixing them. Returns exit code 1 if any issues found.
.EXAMPLE
    .\format-specs.ps1
    .\format-specs.ps1 -Check
#>
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$cliProject = Join-Path $repoRoot 'src' 'BattleScribeSpec.Cli' 'BattleScribeSpec.Cli.csproj'
$specsDir = Join-Path $repoRoot 'specs'

if (-not (Test-Path $specsDir)) {
    Write-Error "specs/ directory not found at $specsDir"
    exit 1
}

$formatArgs = @('run', '--project', $cliProject, '--', 'format', $specsDir)
if ($Check) {
    $formatArgs += '--check'
}

dotnet @formatArgs
exit $LASTEXITCODE
