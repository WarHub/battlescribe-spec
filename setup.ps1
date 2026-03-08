<#
.SYNOPSIS
    Sets up external dependencies needed for the full test suite.

.DESCRIPTION
    Clones sibling repositories required by battlescribe-spec:
    - wham (WarHub ArmouryModel) — build dependency, referenced via ProjectReference
    - wh40k-9e (BSData) — real-world test data for integration tests

    Downloads external test data artifacts pinned in testdata.json:
    - newrecruit-har — frozen HAR snapshot from WarHub/newrecruit-har GitHub Releases

    Installs Playwright browsers needed for New Recruit adapter tests.

    Repositories are cloned as siblings to the battlescribe-spec repo root.
    Test data is downloaded into .testdata/<key>/.
    Already-present items are skipped.

    Requires the GitHub CLI (gh) for test data downloads.
    Requires the .NET SDK (dotnet) for Playwright browser installation.

.PARAMETER Force
    Re-download test data and re-install Playwright browsers even if already present.

.PARAMETER SkipPlaywright
    Skip Playwright browser installation.

.EXAMPLE
    ./setup.ps1

.EXAMPLE
    ./setup.ps1 -Force

.EXAMPLE
    ./setup.ps1 -SkipPlaywright
#>
param(
    [switch]$Force,
    [switch]$SkipPlaywright
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

# --- Playwright browsers ---

if ($SkipPlaywright) {
    Write-Host ""
    Write-Host "Skipping Playwright browser installation (-SkipPlaywright)" -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "Installing Playwright browsers..." -ForegroundColor Cyan

    $nrProject = Join-Path $repoRoot 'src/BattleScribeSpec.NewRecruit/BattleScribeSpec.NewRecruit.csproj'

    # Restore NuGet packages so we can evaluate the Playwright package path
    dotnet restore $nrProject -v q
    if ($LASTEXITCODE -ne 0) { throw "Failed to restore NR project" }

    # Get the Playwright NuGet package path via GeneratePathProperty
    $pkg = (dotnet msbuild $nrProject -getProperty:PkgMicrosoft_Playwright -nologo -restore:false 2>$null `
        | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 1).Trim()
    if (-not $pkg -or -not (Test-Path $pkg)) {
        throw "Could not resolve PkgMicrosoft_Playwright (got: '$pkg')"
    }

    # Read chromium revision from the package's browsers.json
    $browsersJson = Get-Content (Join-Path $pkg '.playwright/package/browsers.json') -Raw | ConvertFrom-Json
    $chromiumRev = ($browsersJson.browsers | Where-Object { $_.name -eq 'chromium' }).revision

    # Determine browser cache directory (same logic as Playwright's registry)
    if ($Env:PLAYWRIGHT_BROWSERS_PATH) {
        $cacheDir = $Env:PLAYWRIGHT_BROWSERS_PATH
    } elseif ($IsLinux) {
        $cacheDir = Join-Path ($Env:XDG_CACHE_HOME ?? (Join-Path $HOME '.cache')) 'ms-playwright'
    } elseif ($IsMacOS) {
        $cacheDir = Join-Path $HOME 'Library/Caches/ms-playwright'
    } else {
        $cacheDir = Join-Path ($Env:LOCALAPPDATA ?? (Join-Path $HOME 'AppData/Local')) 'ms-playwright'
    }

    $chromiumDir = Join-Path $cacheDir "chromium-$chromiumRev"

    if (-not $Force -and (Test-Path $chromiumDir)) {
        Write-Host "  [OK] Playwright browsers already installed (chromium-$chromiumRev)" -ForegroundColor Green
    } else {
        Write-Host "  Installing browsers (chromium-$chromiumRev)..." -ForegroundColor Yellow
        $Env:PLAYWRIGHT_DRIVER_SEARCH_PATH = $pkg
        $dll = Join-Path $pkg 'lib/netstandard2.0/Microsoft.Playwright.dll'
        [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll)) | Out-Null
        $exitCode = [Microsoft.Playwright.Program]::Main(@('install', '--with-deps'))
        if ($exitCode -ne 0) { throw "Playwright browser install failed (exit code $exitCode)" }
        Write-Host "  [OK] Playwright browsers installed" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Setup complete. All dependencies are ready." -ForegroundColor Cyan
Write-Host ""
Write-Host "To run all tests (including real-world data tests):" -ForegroundColor White
Write-Host "  dotnet test" -ForegroundColor White
