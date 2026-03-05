<#
.SYNOPSIS
    Downloads external test data artifacts pinned in testdata.json.

.DESCRIPTION
    Reads testdata.json and downloads each entry's GitHub Release assets
    into .testdata/<key>/. Skips entries already downloaded with a matching tag.

    Requires the GitHub CLI (gh) to be installed and authenticated.

.EXAMPLE
    ./testdata-setup.ps1

.EXAMPLE
    ./testdata-setup.ps1 -Force
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$configPath = Join-Path $repoRoot 'testdata.json'
$testdataDir = Join-Path $repoRoot '.testdata'

if (-not (Test-Path $configPath)) {
    Write-Host "No testdata.json found at $configPath — nothing to download." -ForegroundColor Yellow
    exit 0
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json

foreach ($key in $config.PSObject.Properties.Name) {
    $entry = $config.$key
    $repo = $entry.repo
    $tag = $entry.tag
    $destDir = Join-Path $testdataDir $key

    Write-Host "[$key] repo=$repo tag=$tag" -ForegroundColor Cyan

    # Check if already downloaded with matching tag
    $metadataPath = Join-Path $destDir 'metadata.json'
    if (-not $Force -and (Test-Path $metadataPath)) {
        $tagMarker = Join-Path $destDir '.tag'
        if ((Test-Path $tagMarker) -and ((Get-Content $tagMarker -Raw).Trim() -eq $tag)) {
            Write-Host "  [OK] Already downloaded ($tag)" -ForegroundColor Green
            continue
        }
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
    $tag | Out-File -FilePath (Join-Path $destDir '.tag') -NoNewline -Encoding utf8

    Write-Host "  [OK] Downloaded to $destDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "Test data setup complete." -ForegroundColor Cyan
