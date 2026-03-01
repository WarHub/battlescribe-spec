<#
.SYNOPSIS
    Sets up external dependencies needed for the full test suite.

.DESCRIPTION
    Clones sibling repositories required by battlescribe-spec:
    - wham (WarHub ArmouryModel) — build dependency, referenced via ProjectReference
    - wh40k-9e (BSData) — real-world test data for integration tests

    Repositories are cloned as siblings to the battlescribe-spec repo root.
    Already-cloned repositories are skipped.

.EXAMPLE
    ./setup.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$parentDir = Split-Path $repoRoot -Parent

Write-Host "Setting up battlescribe-spec dependencies..." -ForegroundColor Cyan
Write-Host "  Repo root:  $repoRoot"
Write-Host "  Parent dir: $parentDir"
Write-Host ""

# wham — build dependency (ProjectReference)
$whamDir = Join-Path $parentDir "wham"
if (Test-Path $whamDir) {
    Write-Host "[OK] wham already exists at $whamDir" -ForegroundColor Green
} else {
    Write-Host "Cloning wham..." -ForegroundColor Yellow
    git clone https://github.com/WarHub/wham.git $whamDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone wham" }
    Write-Host "[OK] wham cloned to $whamDir" -ForegroundColor Green
}

# wh40k-9e — real-world test data
$wh40kDir = Join-Path $parentDir "wh40k-9e"
if (Test-Path $wh40kDir) {
    Write-Host "[OK] wh40k-9e already exists at $wh40kDir" -ForegroundColor Green
} else {
    Write-Host "Cloning wh40k-9e (shallow, tag v9.8.0)..." -ForegroundColor Yellow
    git clone --depth 1 --branch v9.8.0 https://github.com/BSData/wh40k-9e.git $wh40kDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone wh40k-9e" }
    Write-Host "[OK] wh40k-9e cloned to $wh40kDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "Setup complete. All dependencies are ready." -ForegroundColor Cyan
Write-Host ""
Write-Host "To run all tests (including real-world data tests):" -ForegroundColor White
Write-Host "  dotnet test" -ForegroundColor White
