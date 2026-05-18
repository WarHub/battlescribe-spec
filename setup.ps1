<#
.SYNOPSIS
    Sets up external dependencies needed for the full test suite.

.DESCRIPTION
    Initializes git submodules (wham — build dependency at .deps/wham).

    Downloads external test data into .testdata/ and other configured locations:
    - wh40k-9e (BSData) — real-world test data for integration tests (git clone)
    - Artifacts pinned in testdata.json (e.g., newrecruit-har — frozen HAR snapshot,
      battlescribe-app — extracted to lib/battlescribe)

    Installs Playwright browsers needed for New Recruit adapter tests.

    Test data is downloaded/cloned into .testdata/<key>/ unless testdata.json overrides
    the destination path.
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

Write-Host "Setting up battlescribe-spec dependencies..." -ForegroundColor Cyan
Write-Host "  Repo root: $repoRoot"
Write-Host ""

# --- Git submodules ---

Write-Host "Initializing git submodules (wham)..." -ForegroundColor Cyan
git -C $repoRoot submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw "Failed to initialize git submodules" }
Write-Host "[OK] Git submodules initialized" -ForegroundColor Green

# --- Test data ---

$configPath = Join-Path $repoRoot 'testdata.json'
$testdataDir = Join-Path $repoRoot '.testdata'

# wh40k-9e — real-world test data (git clone)
$wh40kTag = 'v9.8.0'
$wh40kDir = Join-Path $testdataDir 'wh40k-9e'
$wh40kTagMarker = Join-Path $wh40kDir '.tag'

if (-not $Force -and (Test-Path $wh40kTagMarker) -and ((Get-Content $wh40kTagMarker -Raw).Trim() -eq $wh40kTag)) {
    Write-Host "[OK] wh40k-9e already cloned ($wh40kTag)" -ForegroundColor Green
} else {
    if (Test-Path $wh40kDir) { Remove-Item $wh40kDir -Recurse -Force }
    Write-Host "Cloning wh40k-9e (shallow, tag $wh40kTag)..." -ForegroundColor Yellow
    git clone --depth 1 --branch $wh40kTag https://github.com/BSData/wh40k-9e.git $wh40kDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone wh40k-9e" }
    $wh40kTag | Out-File -FilePath $wh40kTagMarker -NoNewline -Encoding utf8
    Write-Host "[OK] wh40k-9e cloned to $wh40kDir" -ForegroundColor Green
}

if (Test-Path $configPath) {
    Write-Host ""
    Write-Host "Downloading test data from testdata.json..." -ForegroundColor Cyan

    $config = Get-Content $configPath -Raw | ConvertFrom-Json

    foreach ($key in $config.PSObject.Properties.Name) {
        $entry = $config.$key
        $repo = $entry.repo
        $destDir = if ($entry.PSObject.Properties['path']) {
            Join-Path $repoRoot $entry.path
        } else {
            Join-Path $testdataDir $key
        }

        $entryType = if ($entry.PSObject.Properties['type']) { $entry.type } else { 'release' }

        switch ($entryType) {
            'release' {
                $tag = $entry.tag
                $pattern = if ($entry.PSObject.Properties['pattern']) { $entry.pattern } else { $null }
                Write-Host "[$key] release: $repo @ $tag" -ForegroundColor Cyan

                $tagMarker = Join-Path $destDir '.tag'
                if (-not $Force -and (Test-Path $tagMarker) -and ((Get-Content $tagMarker -Raw).Trim() -eq $tag)) {
                    Write-Host "  [OK] Already downloaded ($tag)" -ForegroundColor Green
                    continue
                }

                if (Test-Path $destDir) { Remove-Item $destDir -Recurse -Force }
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null

                Write-Host "  Downloading release $tag from $repo..." -ForegroundColor Yellow
                $dlArgs = @('release', 'download', $tag, '-R', $repo, '-D', $destDir)
                if ($pattern) { $dlArgs += @('--pattern', $pattern) }
                gh @dlArgs
                if ($LASTEXITCODE -ne 0) { throw "Failed to download release $tag from $repo" }

                # Extract ZIP archives and remove the archive file
                $zipFiles = Get-ChildItem -Path $destDir -Filter '*.zip'
                foreach ($zip in $zipFiles) {
                    Write-Host "  Extracting $($zip.Name)..." -ForegroundColor Yellow
                    if ($IsLinux -or $IsMacOS) {
                        # Use system unzip to preserve execute permissions
                        & unzip -q $zip.FullName -d $destDir
                        if ($LASTEXITCODE -ne 0) { throw "Failed to extract $($zip.Name)" }
                    } else {
                        Expand-Archive -Path $zip.FullName -DestinationPath $destDir -Force
                    }
                    Remove-Item $zip.FullName -Force
                }

                $tag | Out-File -FilePath $tagMarker -NoNewline -Encoding utf8
                Write-Host "  [OK] Downloaded to $destDir" -ForegroundColor Green
            }
            'archive' {
                $commit = $entry.commit
                $ref = if ($entry.PSObject.Properties['ref']) { $entry.ref } else { $null }
                Write-Host "[$key] archive: $repo @ $($commit.Substring(0, 12))" -ForegroundColor Cyan

                $tagMarker = Join-Path $destDir '.tag'
                if (-not $Force -and (Test-Path $tagMarker) -and ((Get-Content $tagMarker -Raw).Trim() -eq $commit)) {
                    Write-Host "  [OK] Already downloaded ($($commit.Substring(0, 12)))" -ForegroundColor Green
                    continue
                }

                if (Test-Path $destDir) { Remove-Item $destDir -Recurse -Force }

                # Clone at specific branch (shallow) then verify commit matches
                $cloneArgs = @('clone', '--depth', '1')
                if ($ref) { $cloneArgs += @('--branch', $ref) }
                $cloneArgs += @("https://github.com/$repo.git", $destDir)
                $refLabel = if ($ref) { $ref } else { 'default' }
                Write-Host "  Cloning $repo ($refLabel)..." -ForegroundColor Yellow
                git @cloneArgs
                if ($LASTEXITCODE -ne 0) { throw "Failed to clone $repo" }

                # Verify commit SHA — write actual commit to .tag (not expected)
                $actual = (git -C $destDir rev-parse HEAD).Trim()
                if ($actual -ne $commit) {
                    Write-Warning "  Expected commit $($commit.Substring(0, 12)) but got $($actual.Substring(0, 12))"
                    Write-Warning "  The pinned commit may be outdated. Update testdata.json to match."
                }

                # Remove .git to save space — we only need the static files
                $gitDir = Join-Path $destDir '.git'
                if (Test-Path $gitDir) { Remove-Item $gitDir -Recurse -Force }

                $actual | Out-File -FilePath $tagMarker -NoNewline -Encoding utf8
                Write-Host "  [OK] Downloaded to $destDir" -ForegroundColor Green
            }
            default {
                Write-Warning "[$key] Unknown type '$entryType' — skipping"
            }
        }
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
