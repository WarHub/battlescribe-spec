<#
.SYNOPSIS
    Measures the wall-clock benefit of bs-engine-host's warm-reuse (one engine instance kept
    alive across a spec batch, reset via Cleanup() between specs) against the cold baseline
    (dispose + recreate per spec) for a given engine/domain/filter.

.DESCRIPTION
    Builds the solution, then runs the SAME batch twice via `bs-spec run --all`:
      - warm: default behavior (whatever ServeCommand.BuildOptions enables for this engine)
      - cold: BSSPEC_DISABLE_WARM_REUSE=1 forces every domain cold, regardless of engine identity
    Each run is timed with Measure-Command (wall clock, includes process startup/teardown).
    Per-spec PASS/FAIL verdicts are compared between warm and cold; a mismatch is a correctness
    regression (warm-reuse must never change conformance results) and is reported loudly.

    See docs/warm-reuse.md for what warm-reuse is and which engines/domains support it.

.PARAMETER Engine
    Built-in engine name (battlescribe, battlescribe-ui, newrecruit, newrecruit-ui).

.PARAMETER Domain
    Which domain to benchmark: roster or gamedata.

.PARAMETER Filter
    --filter value passed to `bs-spec run --all` (comma-separated category/id substrings, OR
    logic). Keep this small (~8 specs) — the point is measuring per-spec overhead, not running
    the full suite.

.PARAMETER Workers
    Passed as --workers to both runs. Defaults to 1 — warm-reuse is a single-engine-instance
    optimization, so parallelism would obscure the very thing being measured.

.EXAMPLE
    pwsh -File scripts/bench-warm-reuse.ps1 -Engine newrecruit-ui -Domain roster -Filter "gamesystem,entry-group"

.EXAMPLE
    pwsh -File scripts/bench-warm-reuse.ps1 -Engine battlescribe-ui -Domain gamedata -Filter "cost,links"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Engine,

    [Parameter(Mandatory = $true)]
    [ValidateSet('roster', 'gamedata')]
    [string]$Domain,

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [int]$Workers = 1
)

$ErrorActionPreference = 'Stop'
# Force invariant (dot decimal) number formatting regardless of the host's locale, so the
# printed table is unambiguous everywhere it runs (e.g. avoids "44,9" on non-en-US systems).
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

$repoRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $repoRoot 'artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll'

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

Write-Section "Build"
Push-Location $repoRoot
try {
    dotnet build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $dll)) {
    throw "bs-spec.dll not found at $dll after build."
}

# Runs one warm-or-cold batch, capturing stdout (the --output json report) and stderr (progress
# / engine-launch diagnostics) separately so a launch failure can be distinguished from ordinary
# spec failures (both would otherwise look like a nonzero exit code).
function Invoke-Batch([bool]$Cold) {
    $label = if ($Cold) { 'COLD' } else { 'WARM' }
    Write-Section "Run: $label ($Engine / $Domain, filter='$Filter')"

    $prevDisable = $env:BSSPEC_DISABLE_WARM_REUSE
    try {
        if ($Cold) {
            $env:BSSPEC_DISABLE_WARM_REUSE = '1'
        }
        else {
            Remove-Item Env:\BSSPEC_DISABLE_WARM_REUSE -ErrorAction SilentlyContinue
        }

        $stdoutFile = New-TemporaryFile
        $stderrFile = New-TemporaryFile
        $domainFlag = "--$Domain"

        $elapsed = Measure-Command {
            & dotnet $dll run --all --engine $Engine $domainFlag --filter $Filter `
                --expected-failures $Engine --workers $Workers --output json `
                1> $stdoutFile.FullName 2> $stderrFile.FullName
            $script:lastExit = $LASTEXITCODE
        }

        $stdout = Get-Content $stdoutFile.FullName -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content $stderrFile.FullName -Raw -ErrorAction SilentlyContinue
        Remove-Item $stdoutFile.FullName, $stderrFile.FullName -ErrorAction SilentlyContinue

        Write-Host "  exit code: $lastExit, wall: $($elapsed.TotalSeconds.ToString('F1', $invariant))s"
        if ($stderr) {
            Write-Host "  --- stderr (tail) ---"
            ($stderr -split "`n" | Select-Object -Last 15) -join "`n" | Write-Host
        }

        if ([string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host "  --- stdout was empty; full stderr follows ---" -ForegroundColor Red
            Write-Host $stderr
            throw "$label run for engine '$Engine' produced no JSON report on stdout — the engine likely failed to launch. See stderr above."
        }

        $report = $null
        try {
            $report = $stdout | ConvertFrom-Json
        }
        catch {
            Write-Host "  --- unparseable stdout ---" -ForegroundColor Red
            Write-Host $stdout
            throw "$label run for engine '$Engine' did not produce valid JSON — the engine likely failed to launch or crashed mid-run. Parse error: $($_.Exception.Message)"
        }

        if ($null -eq $report.specs -or $report.specs.Count -eq 0) {
            throw "$label run for engine '$Engine' matched zero specs with filter '$Filter' — check the filter and that the domain is supported."
        }

        return [pscustomobject]@{
            Label      = $label
            WallSeconds = $elapsed.TotalSeconds
            SpecCount  = $report.specs.Count
            Verdicts   = @{}
            Report     = $report
        } | ForEach-Object {
            foreach ($s in $report.specs) {
                $_.Verdicts["$($s.category)/$($s.id)"] = [bool]$s.passed
            }
            $_
        }
    }
    finally {
        if ($null -ne $prevDisable) {
            $env:BSSPEC_DISABLE_WARM_REUSE = $prevDisable
        }
        else {
            Remove-Item Env:\BSSPEC_DISABLE_WARM_REUSE -ErrorAction SilentlyContinue
        }
    }
}

$warm = Invoke-Batch -Cold $false
$cold = Invoke-Batch -Cold $true

Write-Section "Verdict comparison (warm vs cold must be identical)"
$mismatches = @()
foreach ($key in $warm.Verdicts.Keys) {
    if (-not $cold.Verdicts.ContainsKey($key)) {
        $mismatches += "  $key : warm ran it, cold did not"
        continue
    }

    if ($warm.Verdicts[$key] -ne $cold.Verdicts[$key]) {
        $warmStr = if ($warm.Verdicts[$key]) { 'PASS' } else { 'FAIL' }
        $coldStr = if ($cold.Verdicts[$key]) { 'PASS' } else { 'FAIL' }
        $mismatches += "  $key : warm=$warmStr cold=$coldStr"
    }
}
foreach ($key in $cold.Verdicts.Keys) {
    if (-not $warm.Verdicts.ContainsKey($key)) {
        $mismatches += "  $key : cold ran it, warm did not"
    }
}

if ($mismatches.Count -gt 0) {
    Write-Host ''
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    Write-Host "!! WARM vs COLD VERDICT MISMATCH — warm-reuse changed conformance results!    !!" -ForegroundColor Red
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    $mismatches | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
}
else {
    Write-Host "  OK — all $($warm.Verdicts.Count) spec verdicts match between warm and cold." -ForegroundColor Green
}

Write-Section "Results"

$specCount = $warm.SpecCount
$warmWall = $warm.WallSeconds
$coldWall = $cold.WallSeconds
$absSaving = $coldWall - $warmWall
$perSpecSaving = if ($specCount -gt 0) { $absSaving / $specCount } else { 0 }
$speedup = if ($warmWall -gt 0) { $coldWall / $warmWall } else { 0 }

$table = [pscustomobject]@{
    Engine              = $Engine
    Domain              = $Domain
    'Spec count'        = $specCount
    'Warm wall (s)'     = $warmWall.ToString('F1', $invariant)
    'Cold wall (s)'     = $coldWall.ToString('F1', $invariant)
    'Abs. saving (s)'   = $absSaving.ToString('F1', $invariant)
    'Per-spec saving(s)' = $perSpecSaving.ToString('F2', $invariant)
    'Speedup (x)'       = $speedup.ToString('F2', $invariant)
}
$table | Format-List

if ($mismatches.Count -gt 0) {
    exit 1
}

exit 0
