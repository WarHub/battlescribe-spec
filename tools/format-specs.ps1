<#
.SYNOPSIS
    Auto-formats spec YAML files to match SpecLintTests conventions.
.DESCRIPTION
    Fixes auto-fixable formatting issues in specs/ YAML files:
    - Ensures blank line before setup: block
    - Ensures blank lines between step items (action/expectedState)
    - Removes trailing whitespace
    - Ensures file ends with a single newline

    Run SpecLintTests after to verify all rules pass:
      dotnet test tests\BattleScribeSpec.Tests.csproj --filter "DisplayName~SpecLint"
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
$specsDir = Join-Path $repoRoot 'specs' 'roster'

if (-not (Test-Path $specsDir)) {
    Write-Error "specs/roster/ directory not found at $specsDir"
    exit 1
}

$files = Get-ChildItem -Path $specsDir -Filter '*.yaml' -Recurse
$totalFixes = 0
$filesFixed = 0

foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    # Normalize to LF for processing
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $original = $text
    $lines = $text -split "`n"
    $result = @()
    $inSteps = $false
    $fixes = @()

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $trimmed = $line.TrimEnd()
        $stripped = $trimmed.Trim()

        # Fix: trailing whitespace
        if ($line -ne $trimmed -and $trimmed.Length -gt 0) {
            $fixes += "line $($i+1): trailing whitespace"
            $line = $trimmed
        }
        # Also trim blank lines to truly empty
        if ($stripped -eq '' -and $line -ne '') {
            $line = ''
        }

        # Fix: remove redundant 'hidden: false' (defaults to false everywhere)
        if ($stripped -eq 'hidden: false') {
            $fixes += "line $($i+1): removed redundant hidden: false"
            continue
        }

        # Fix: blank line before setup:
        if ($stripped -eq 'setup:' -and $result.Count -gt 0) {
            $prev = $result[$result.Count - 1].Trim()
            if ($prev -ne '') {
                $result += ''
                $fixes += "line $($i+1): added blank line before setup:"
            }
        }

        # Track steps section
        if ($stripped -eq 'steps:') {
            $inSteps = $true
            $result += $line
            continue
        }

        # Fix: blank line between steps
        # NOTE: hardcodes 2-space indent — safe because SpecLintTests enforce
        # exactly 2-space YAML indentation across all spec files.
        if ($inSteps -and $line -match '^  - (action|expectedState):') {
            if ($result.Count -gt 0) {
                $prev = $result[$result.Count - 1].Trim()
                if ($prev -ne '' -and $prev -ne 'steps:' -and -not $prev.StartsWith('#')) {
                    $result += ''
                    $fixes += "line $($i+1): added blank line before step"
                }
            }
        }

        $result += $line
    }

    $newText = ($result -join "`n")

    # Fix: ensure file ends with exactly one newline
    $trimmed = $newText.TrimEnd("`n", "`r")
    $normalized = $trimmed + "`n"
    if ($normalized -ne $newText) {
        $fixes += "trailing newline normalization"
    }
    $newText = $normalized

    if ($newText -ne $original) {
        $filesFixed++
        $totalFixes += $fixes.Count
        $rel = $file.FullName.Substring($specsDir.Length + 1).Replace('\', '/')
        if ($Check) {
            if ($fixes.Count -gt 0) {
                Write-Host "  $rel" -ForegroundColor Yellow
                foreach ($fix in $fixes) {
                    Write-Host "    $fix" -ForegroundColor DarkYellow
                }
            }
        } else {
            [System.IO.File]::WriteAllText($file.FullName, $newText)
            Write-Host "  $rel ($($fixes.Count) fixes)" -ForegroundColor Green
        }
    }
}

if ($Check) {
    if ($totalFixes -gt 0) {
        Write-Host "`n$totalFixes issue(s) in $filesFixed file(s). Run format-specs.ps1 to fix." -ForegroundColor Red
        exit 1
    } else {
        Write-Host "All $($files.Count) spec files are correctly formatted." -ForegroundColor Green
        exit 0
    }
} else {
    if ($totalFixes -gt 0) {
        Write-Host "`nFixed $totalFixes issue(s) in $filesFixed file(s)." -ForegroundColor Cyan
    } else {
        Write-Host "All $($files.Count) spec files are correctly formatted." -ForegroundColor Green
    }
}
