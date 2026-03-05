<#
.SYNOPSIS
    Sets up external dependencies needed for the full test suite.

.DESCRIPTION
    Clones sibling repositories required by battlescribe-spec:
    - wham (WarHub ArmouryModel) — build dependency, referenced via ProjectReference
    - wh40k-9e (BSData) — real-world test data for integration tests

    Downloads external test data artifacts pinned in testdata.json:
    - newrecruit-har — frozen HAR snapshot from WarHub/newrecruit-har GitHub Releases

    Repositories are cloned as siblings to the battlescribe-spec repo root.
    Test data is downloaded into .testdata/<key>/.
    Already-present items are skipped.

    Requires the GitHub CLI (gh) for test data downloads.

.PARAMETER Force
    Re-download test data even if already present with matching tag.

.EXAMPLE
    ./setup.ps1

.EXAMPLE
    ./setup.ps1 -Force
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$parentDir = Split-Path $repoRoot -Parent

Write-Host "Setting up battlescribe-spec dependencies..." -ForegroundColor Cyan
Write-Host "  Repo root:  $repoRoot"
Write-Host "  Parent dir: $parentDir"
Write-Host ""

# --- Sibling repositories ---

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

# --- Test data from testdata.json ---

$configPath = Join-Path $repoRoot 'testdata.json'
$testdataDir = Join-Path $repoRoot '.testdata'

if (Test-Path $configPath) {
    Write-Host ""
    Write-Host "Downloading test data from testdata.json..." -ForegroundColor Cyan

    $config = Get-Content $configPath -Raw | ConvertFrom-Json

    foreach ($key in $config.PSObject.Properties.Name) {
        $entry = $config.$key
        $repo = $entry.repo
        $tag = $entry.tag
        $destDir = Join-Path $testdataDir $key

        Write-Host "[$key] repo=$repo tag=$tag" -ForegroundColor Cyan

        # Check if already downloaded with matching tag
        $tagMarker = Join-Path $destDir '.tag'
        if (-not $Force -and (Test-Path $tagMarker) -and ((Get-Content $tagMarker -Raw).Trim() -eq $tag)) {
            Write-Host "  [OK] Already downloaded ($tag)" -ForegroundColor Green
            continue
        }

        # Clean and recreate destination
        if (Test-Path $destDir) {
            Remove-Item $destDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null

        Write-Host "  Downloading release $tag from $repo..." -ForegroundColor Yellow
        gh release download $tag -R $repo -D $destDir
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download release $tag from $repo"
        }

        # Write tag marker for skip-check
        $tag | Out-File -FilePath $tagMarker -NoNewline -Encoding utf8

        Write-Host "  [OK] Downloaded to $destDir" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Setup complete. All dependencies are ready." -ForegroundColor Cyan
Write-Host ""
Write-Host "To run all tests (including real-world data tests):" -ForegroundColor White
Write-Host "  dotnet test" -ForegroundColor White
