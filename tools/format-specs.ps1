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

        # Fix: remove redundant 'engines: {}' (empty engines is same as omitting)
        if ($stripped -eq 'engines: {}') {
            $fixes += "line $($i+1): removed redundant engines: {}"
            continue
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

    # Fix: reorder expectedState properties (errors first, forces second-last, engines last)
    # Zone 0: errors/errorsContain, Zone 1: everything else, Zone 2: forces, Zone 3: engines
    # Process line-by-line to identify expectedState blocks and reorder their
    # top-level properties without disturbing blank lines/comments outside them.
    $textLines = $newText -split "`n"
    $reordered = $false
    $i2 = 0
    while ($i2 -lt $textLines.Length) {
        if ($textLines[$i2] -match '^\s\s- expectedState:$') {
            $esLine = $i2
            $i2++
            # Collect property blocks: each starts at 6-space indent key
            $propBlocks = @()
            $currentName = $null
            $currentBlockLines = @()
            while ($i2 -lt $textLines.Length) {
                $l = $textLines[$i2]
                # Stop at next step item or non-indented line
                if ($l -match '^  - (action|expectedState):' -or ($l -match '^\S' -and $l -ne '')) {
                    break
                }
                # Blank lines at end of expectedState = separator before next step
                # We peek: if next non-blank is a step item, stop here
                if ($l -eq '') {
                    # Check if all remaining lines until next content are blank + step
                    $peekIdx = $i2 + 1
                    while ($peekIdx -lt $textLines.Length -and $textLines[$peekIdx] -eq '') {
                        $peekIdx++
                    }
                    if ($peekIdx -ge $textLines.Length -or $textLines[$peekIdx] -match '^  - (action|expectedState):' -or $textLines[$peekIdx] -match '^  #' -or $textLines[$peekIdx] -match '^\S') {
                        break
                    }
                }
                # Top-level property (6 spaces, not deeper)
                if ($l -match '^      [a-z]\w*:' -and $l -notmatch '^        ') {
                    if ($null -ne $currentName) {
                        $propBlocks += @{ Name = $currentName; Lines = $currentBlockLines }
                    }
                    $currentName = ($l.TrimStart() -split ':')[0]
                    $currentBlockLines = @($l)
                } else {
                    $currentBlockLines += $l
                }
                $i2++
            }
            if ($null -ne $currentName) {
                $propBlocks += @{ Name = $currentName; Lines = $currentBlockLines }
            }

            # Check if zone ordering needs fixing
            if ($propBlocks.Count -ge 2) {
                $zones = @($propBlocks | ForEach-Object {
                    switch ($_.Name) {
                        { $_ -in @('errors', 'errorsContain') } { 0 }
                        'forces' { 2 }
                        'engines' { 3 }
                        default { 1 }
                    }
                })
                $needsFix = $false
                $maxZone = -1
                foreach ($z in $zones) {
                    if ($z -lt $maxZone) { $needsFix = $true; break }
                    if ($z -gt $maxZone) { $maxZone = $z }
                }
                if ($needsFix) {
                    # Stable sort by zone
                    $indexed = @()
                    for ($k = 0; $k -lt $propBlocks.Count; $k++) {
                        $indexed += @{ Block = $propBlocks[$k]; Zone = $zones[$k]; Idx = $k }
                    }
                    $sortedBlocks = @($indexed | Sort-Object { $_.Zone }, { $_.Idx } | ForEach-Object { $_.Block })
                    # Replace lines after expectedState: with sorted blocks
                    $newBlockLines = @()
                    foreach ($sb in $sortedBlocks) {
                        $newBlockLines += $sb.Lines
                    }
                    # Replace in textLines
                    $replaceStart = $esLine + 1
                    $replaceEnd = $i2 - 1
                    $before = $textLines[0..$esLine]
                    $after = if ($i2 -lt $textLines.Length) { $textLines[$i2..($textLines.Length - 1)] } else { @() }
                    $textLines = $before + $newBlockLines + $after
                    $i2 = $esLine + 1 + $newBlockLines.Count
                    $reordered = $true
                }
            }
        } else {
            $i2++
        }
    }
    if ($reordered) {
        $newText = $textLines -join "`n"
        $fixes += "expectedState property reorder"
    }

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
