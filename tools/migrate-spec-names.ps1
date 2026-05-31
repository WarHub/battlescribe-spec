#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Removes generic boilerplate game system / catalogue names from roster spec YAMLs.
    After this migration, game system name/id and catalogue name/gameSystemId are
    auto-derived from the spec ID by SpecLoader.GetSetupData().

.DESCRIPTION
    Removes:
      - gameSystem id: test-gs
      - gameSystem name: Test GS / Test Game System
      - catalogue gameSystemId: test-gs
      - catalogue name: Cat / Test Catalogue  (single-catalogue specs only)

    Multi-catalogue specs keep their catalogue names (they're already meaningful).

.PARAMETER SpecsDir
    Path to the specs/roster directory. Defaults to the repo's specs/roster.
.PARAMETER DryRun
    Print changes without writing files.
#>
param(
    [string]$SpecsDir = (Join-Path $PSScriptRoot ".." "specs" "roster"),
    [switch]$DryRun
)

$SpecsDir = Resolve-Path $SpecsDir

# Generic game system id/name values to remove
$genericGsIds   = @("test-gs")
$genericGsNames = @("Test GS", "Test Game System")
# Generic catalogue name values to remove (only in single-catalogue specs)
$genericCatNames = @("Cat", "Test Catalogue")

$files = Get-ChildItem $SpecsDir -Recurse -Filter "*.yaml"
$changedCount = 0

foreach ($file in $files) {
    $original = [System.IO.File]::ReadAllText($file.FullName)
    $lines = $original -split "`n"

    # Count catalogues by counting "    - id:" lines (4 spaces + "- id:")
    $catCount = ($lines | Where-Object { $_ -match '^\s{4}-\s+id:' }).Count
    $isSingleCatalogue = $catCount -le 1

    $newLines = [System.Collections.Generic.List[string]]::new()
    $changed = $false

    # Track whether we are inside the gameSystem block (lines 4-space indented after gameSystem:)
    # We detect context by checking line patterns directly.

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # Remove gameSystem id: test-gs  (exactly 4-space indented under gameSystem)
        if ($line -match '^    id: (' + ($genericGsIds -join '|') + ')\s*$') {
            # Verify context: previous non-empty lines should include "gameSystem:"
            # Quick heuristic: this pattern only appears in the gameSystem block
            $changed = $true
            if ($DryRun) { Write-Host "  REMOVE: $($file.Name):$($i+1): $line" }
            continue
        }

        # Remove gameSystem name: Test GS / Test Game System (4-space indented)
        $escapedGsNames = $genericGsNames | ForEach-Object { [regex]::Escape($_) }
        if ($line -match '^    name: (' + ($escapedGsNames -join '|') + ')\s*$') {
            $changed = $true
            if ($DryRun) { Write-Host "  REMOVE: $($file.Name):$($i+1): $line" }
            continue
        }

        # Remove catalogue gameSystemId: test-gs (6-space indented)
        if ($line -match '^      gameSystemId: (' + ($genericGsIds -join '|') + ')\s*$') {
            $changed = $true
            if ($DryRun) { Write-Host "  REMOVE: $($file.Name):$($i+1): $line" }
            continue
        }

        # Remove catalogue name: Cat / Test Catalogue — only in single-catalogue specs
        if ($isSingleCatalogue) {
            $escapedCatNames = $genericCatNames | ForEach-Object { [regex]::Escape($_) }
            if ($line -match '^      name: (' + ($escapedCatNames -join '|') + ')\s*$') {
                $changed = $true
                if ($DryRun) { Write-Host "  REMOVE: $($file.Name):$($i+1): $line" }
                continue
            }
        }

        $newLines.Add($line)
    }

    if ($changed) {
        $changedCount++
        if ($DryRun) {
            Write-Host "[dry-run] Would update: $($file.FullName)"
        } else {
            $newContent = $newLines -join "`n"
            $encoding = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($file.FullName, $newContent, $encoding)
        }
    }
}

Write-Host ""
if ($DryRun) {
    Write-Host "Dry run complete. $changedCount file(s) would be updated."
} else {
    Write-Host "Migration complete. $changedCount file(s) updated."
}
