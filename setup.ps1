<#
.SYNOPSIS
    Sets up external dependencies needed for the full test suite.

.DESCRIPTION
    Initializes git submodules (wham — build dependency at .deps/wham).

    Downloads external test data into .testdata/ and other configured locations:
    - wh40k-9e (BSData) — real-world test data for integration tests (git clone)
    - Artifacts pinned in testdata.json (e.g., newrecruit-har — frozen HAR snapshot,
      battlescribe-app — extracted to lib/battlescribe)

    testdata.json entries with "type": "archive" (e.g. nr-editor) are checked out at the
    EXACT pinned commit, not at the tip of "ref" — that field only names the branch used to
    recover the commit if the host refuses fetch-by-SHA. If the pinned commit cannot be
    obtained, setup fails and tells you to re-pin; it never falls back to the branch tip.

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

.PARAMETER SkipJavaAgent
    Skip Liberica JDK download and bs-ui-java-agent.jar build.
    Set automatically when running in CI (CI=true env var).

.PARAMETER SkipWh40k
    Skip cloning the wh40k-9e real-world test data. Safe for the fast CI lane and any
    workflow that does not run the real-world integration tests.

.EXAMPLE
    ./setup.ps1

.EXAMPLE
    ./setup.ps1 -Force

.EXAMPLE
    ./setup.ps1 -SkipPlaywright

.EXAMPLE
    ./setup.ps1 -SkipJavaAgent
#>
param(
    [switch]$Force,
    [switch]$SkipPlaywright,
    [switch]$SkipJavaAgent,
    [switch]$SkipWh40k
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot

# Locate a JDK home under $root, tolerating the three extraction layouts we produce:
#   - bin directly under $root            (Linux: tar --strip-components=1)
#   - <subdir>/bin                        (Windows: zip)
#   - <subdir>/Contents/Home/bin          (macOS: zip, .jdk app bundle)
function Resolve-JdkHome {
    param([string]$root)
    if (Test-Path (Join-Path $root 'bin')) { return $root }
    foreach ($dir in (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue)) {
        if (Test-Path (Join-Path $dir.FullName 'bin')) { return $dir.FullName }
        $macHome = Join-Path $dir.FullName 'Contents/Home'
        if (Test-Path (Join-Path $macHome 'bin')) { return $macHome }
    }
    return $null
}

# Materialize $Repo at exactly $Commit under $Destination.
#
# A pin is a CONTRACT, not a hint. The suites that consume these archives are advertised as
# frozen, and a fixture that quietly follows a third party's branch tip is not frozen — it is a
# test against whatever someone else published last night, and the substitution is invisible in
# the results. So this resolves the pinned OBJECT and never the branch, and it throws when it
# cannot: a hard error at setup time is strictly better than a green suite that proves nothing.
function Install-PinnedArchive {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Destination,
        [string]$Ref
    )

    $remote = "https://github.com/$Repo.git"
    $refLabel = if ($Ref) { $Ref } else { 'the default branch' }
    $short = $Commit.Substring(0, 12)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    # core.autocrlf/core.eol are pinned off: this is a byte-for-byte snapshot served to a
    # browser, and it must be the same bytes on Windows, Linux and macOS.
    $git = @('-C', $Destination, '-c', 'init.defaultBranch=main', '-c', 'core.autocrlf=false', '-c', 'core.eol=lf')

    git @git init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Failed to initialize a git repository at $Destination" }
    git @git remote add origin $remote
    if ($LASTEXITCODE -ne 0) { throw "Failed to add remote $remote" }

    # Preferred: ask for the object by SHA. GitHub serves fetch-by-SHA
    # (uploadpack.allowAnySHA1InWant), so one shallow fetch lands the pinned commit even after
    # it has stopped being the branch tip — which `clone --depth 1 --branch <ref>` can never do,
    # because a depth-1 clone of a branch only ever contains that branch's newest commit.
    Write-Host "  Fetching $Repo @ $short..." -ForegroundColor Yellow
    $shaLog = (git @git fetch --depth 1 --no-tags origin $Commit 2>&1) -join [Environment]::NewLine
    $refLog = $null

    # `not our ref` is the server saying the object is not in the repository at all — a full
    # history fetch cannot conjure it either, so skip straight to the re-pin error instead of
    # dragging down every commit on $Ref first. Any OTHER failure (a host that refuses
    # unadvertised objects, a network blip) still gets the fallback: keying on the message
    # costs us a slow path if git ever rewords it, never a wrong answer.
    if ($LASTEXITCODE -ne 0 -and $shaLog -notmatch 'not our ref') {
        # The full history of $Ref still contains the pin whenever it is an ancestor of the tip.
        Write-Host "  Fetch-by-SHA did not succeed — retrying with the full history of $refLabel..." -ForegroundColor Yellow
        $fetchArgs = @('fetch', '--no-tags', 'origin')
        if ($Ref) { $fetchArgs += $Ref }
        $refLog = (git @git @fetchArgs 2>&1) -join [Environment]::NewLine
    }

    # One checkout covers both paths: after the SHA fetch the object is present outright, and
    # after the history fetch it is present iff the pin is reachable from $Ref. Either way this
    # is where an unobtainable pin becomes a hard failure.
    git @git checkout --quiet --detach $Commit 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $indent = { param($t) ($t -split "`r?`n" | ForEach-Object { "      $($_.TrimEnd())" }) -join [Environment]::NewLine }
        $tried = @("  - fetch by SHA (--depth 1 origin $short)", (& $indent $shaLog))
        $tried += if ($refLog) {
            @("  - full history of $refLabel", (& $indent $refLog))
        } else {
            "  - full history of ${refLabel}: skipped, the server reports the object is not in this repository"
        }

        $tip = (git ls-remote $remote ($Ref ? "refs/heads/$Ref" : 'HEAD') 2>&1) -split '\s+' | Select-Object -First 1
        if ($tip -match '^[0-9a-f]{40}$') { $tried += "", "  $refLabel is currently at $tip" }

        throw @"
Could not obtain the pinned commit $Commit of $Repo.

$($tried -join [Environment]::NewLine)

The pin is unreachable upstream — most likely $refLabel was force-pushed and the commit was
garbage-collected. setup.ps1 will NOT substitute the branch tip: a frozen fixture that tracks
someone else's HEAD is not frozen, and nothing downstream can tell the difference.

Re-pin it:
  1. pick a commit from https://github.com/$Repo/commits/$Ref
  2. update "commit" for this entry in testdata.json
  3. re-run ./setup.ps1
"@
    }

    $actual = "$(git @git rev-parse HEAD)".Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Commit) {
        throw "Checked out $Commit of $Repo but HEAD is $actual — refusing to treat this as the pinned snapshot"
    }

    # Only the static files are needed; drop the object store.
    $gitDir = Join-Path $Destination '.git'
    if (Test-Path $gitDir) { Remove-Item $gitDir -Recurse -Force }
}

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

if ($SkipWh40k) {
    Write-Host "[SKIP] wh40k-9e clone (-SkipWh40k)" -ForegroundColor DarkGray
} elseif (-not $Force -and (Test-Path $wh40kTagMarker) -and ((Get-Content $wh40kTagMarker -Raw).Trim() -eq $wh40kTag)) {
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
                # Fetch-by-SHA needs a full SHA over the wire, and only a full SHA can be
                # compared exactly against the resulting HEAD. Reject anything else up front
                # rather than discovering it as a mismatch after the download.
                if ($commit -notmatch '^[0-9a-f]{40}$') {
                    throw "[$key] testdata.json pins commit '$commit' — an archive pin must be a full 40-character lowercase commit SHA"
                }
                Write-Host "[$key] archive: $repo @ $($commit.Substring(0, 12))" -ForegroundColor Cyan

                # The marker holds the PINNED commit, and it is written only after the checkout
                # has been verified to be at it — so a marker hit is proof that the pinned bytes
                # are on disk. It used to hold whatever had actually been cloned, i.e. a log of
                # what happened rather than an assertion of what was required; combined with a
                # warn-and-continue on mismatch, that is how a fixture documented as frozen came
                # to serve the upstream branch tip on every machine and every CI run.
                $tagMarker = Join-Path $destDir '.tag'
                if (-not $Force -and (Test-Path $tagMarker) -and ((Get-Content $tagMarker -Raw).Trim() -eq $commit)) {
                    Write-Host "  [OK] Already downloaded ($($commit.Substring(0, 12)))" -ForegroundColor Green
                    continue
                }

                if (Test-Path $destDir) { Remove-Item $destDir -Recurse -Force }
                Install-PinnedArchive -Repo $repo -Commit $commit -Ref $ref -Destination $destDir

                $commit | Out-File -FilePath $tagMarker -NoNewline -Encoding utf8
                Write-Host "  [OK] Downloaded to $destDir" -ForegroundColor Green
            }
            default {
                Write-Warning "[$key] Unknown type '$entryType' — skipping"
            }
        }
    }
}

# --- ASM (vendored) ---
# Pinned by version and SHA-256, mirroring the download-then-verify contract the rest of this script
# uses for frozen fixtures: a bad or substituted download is a hard failure, never a silent build
# against unexpected bytecode. Vendored BEFORE anything that consumes it: the bs-ui-java-agent build
# below shades ASM into the agent jar, and the bs-engine-patch build after that compiles against it
# (both serve the engine-jar patch of issue #401 -- see src/bs-engine-patch). Not gated by
# -SkipJavaAgent: the offline lanes that skip the agent still build the .NET engine, whose patch
# step needs ASM.

Write-Host ""
Write-Host "Vendoring ASM..." -ForegroundColor Cyan

$asmVersion = '9.7'
$asmSha256 = 'ADF46D5E34940BDF148ECDD26A9EE8EEA94496A72034FF7141066B3EEA5C4E9D'
$asmDir = Join-Path $repoRoot 'lib/asm'
$asmJar = Join-Path $asmDir "asm-$asmVersion.jar"
$asmUrl = "https://repo1.maven.org/maven2/org/ow2/asm/asm/$asmVersion/asm-$asmVersion.jar"

$asmOk = (Test-Path $asmJar) -and
    ((Get-FileHash $asmJar -Algorithm SHA256).Hash -eq $asmSha256)
if (-not $Force -and $asmOk) {
    Write-Host "  [OK] ASM $asmVersion already vendored" -ForegroundColor Green
} else {
    if (Test-Path $asmDir) { Remove-Item $asmDir -Recurse -Force }
    New-Item -ItemType Directory -Path $asmDir -Force | Out-Null
    Write-Host "  Downloading asm-$asmVersion.jar..." -ForegroundColor Yellow
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $asmUrl -OutFile $asmJar -UseBasicParsing
    $ProgressPreference = 'Continue'
    $actual = (Get-FileHash $asmJar -Algorithm SHA256).Hash
    if ($actual -ne $asmSha256) {
        Remove-Item $asmJar -Force
        throw "ASM $asmVersion checksum mismatch: expected $asmSha256, got $actual. Refusing to build against it."
    }
    Write-Host "  [OK] ASM $asmVersion vendored to $asmJar" -ForegroundColor Green
}

# --- Liberica JDK + bs-ui-java-agent build ---
# Skipped in CI (env var CI=true) because CI provisions Java via actions/setup-java.
# Also skipped if -SkipJavaAgent switch is passed.

if ($SkipJavaAgent -or $env:CI -eq 'true') {
    Write-Host ""
    Write-Host "Skipping Liberica JDK / Java agent build (CI or -SkipJavaAgent)" -ForegroundColor Yellow
} else {
    $libericaVersion = '11.0.31+11'
    $libericaDir = Join-Path $repoRoot 'lib/liberica-jdk'
    $libericaTagFile = Join-Path $libericaDir '.tag'

    Write-Host ""
    Write-Host "Setting up Liberica JDK $libericaVersion (for bs-ui-java-agent)..." -ForegroundColor Cyan

    # Re-extract if forced, version-mismatched, OR a legacy non-normalized layout is present
    # (correct version on .tag but no top-level bin/ — e.g. an install from before normalization).
    $libericaReady = (Test-Path $libericaTagFile) -and
        ((Get-Content $libericaTagFile -Raw).Trim() -eq $libericaVersion) -and
        (Test-Path (Join-Path $libericaDir 'bin'))
    if (-not $Force -and $libericaReady) {
        Write-Host "  [OK] Already downloaded ($libericaVersion)" -ForegroundColor Green
    } else {
        if (Test-Path $libericaDir) { Remove-Item $libericaDir -Recurse -Force }
        $staging = Join-Path $libericaDir '.staging'
        New-Item -ItemType Directory -Path $staging -Force | Out-Null

        $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'aarch64' } else { 'amd64' }
        if ($IsLinux) {
            $assetName = "bellsoft-jdk${libericaVersion}-linux-${arch}-full.tar.gz"
        } elseif ($IsMacOS) {
            $assetName = "bellsoft-jdk${libericaVersion}-macos-${arch}-full.zip"
        } else {
            $assetName = "bellsoft-jdk${libericaVersion}-windows-${arch}-full.zip"
        }

        $downloadUrl = "https://download.bell-sw.com/java/${libericaVersion}/${assetName}"
        $archivePath = Join-Path $libericaDir $assetName

        Write-Host "  Downloading $assetName..." -ForegroundColor Yellow
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
        $ProgressPreference = 'Continue'

        # Extract into staging, then normalize so the JDK home is ALWAYS $libericaDir itself
        # (i.e. $libericaDir/bin/java exists on every OS). The archive's internal layout differs
        # per OS — Linux/Windows wrap the JDK in a jdk-*-full/ dir, macOS in a
        # jdk-*-full.jdk/Contents/Home/ app bundle — but downstream consumers see one path.
        Write-Host "  Extracting..." -ForegroundColor Yellow
        if ($assetName.EndsWith('.tar.gz')) {
            tar -xzf $archivePath -C $staging
            if ($LASTEXITCODE -ne 0) { throw "Failed to extract Liberica JDK" }
        } else {
            Expand-Archive -Path $archivePath -DestinationPath $staging -Force
        }
        Remove-Item $archivePath -Force

        $extractedHome = Resolve-JdkHome $staging
        if (-not $extractedHome) {
            throw "Could not locate JDK home inside the extracted archive — extraction may have failed"
        }
        Get-ChildItem -LiteralPath $extractedHome -Force | Move-Item -Destination $libericaDir -Force
        Remove-Item $staging -Recurse -Force

        $libericaVersion | Out-File -FilePath $libericaTagFile -NoNewline -Encoding utf8
        Write-Host "  [OK] Liberica JDK ready at $libericaDir" -ForegroundColor Green
    }

    # Layout is normalized above: the JDK home is $libericaDir, with bin/ directly under it.
    if (-not (Test-Path (Join-Path $libericaDir 'bin'))) {
        throw "Liberica JDK at $libericaDir is missing bin/ — delete it and re-run with -Force"
    }

    Write-Host ""
    Write-Host "Building bs-ui-java-agent..." -ForegroundColor Cyan
    $buildScript = Join-Path $repoRoot 'src/bs-ui-java-agent/build.ps1'
    pwsh -File $buildScript -JavaHome $libericaDir
    if ($LASTEXITCODE -ne 0) { throw "bs-ui-java-agent build failed" }
    Write-Host "[OK] bs-ui-java-agent.jar built" -ForegroundColor Green
}

# --- bs-engine-patch tool ---
# The in-process BattleScribe engine is IKVM-compiled from a COPY of BattleScribeEngine.jar that the
# PatchBattleScribeEngineJar MSBuild target rewrites so validation errors carry their constraint id
# (see src/bs-engine-patch and issue #401). That target runs `java PatchJarMain`, which needs the
# patch tool compiled against the ASM vendored above — produced here so a plain `dotnet build` has
# it. Not gated by -SkipJavaAgent: every lane that builds the .NET engine needs the patched jar,
# including CI's offline `checks` lane. It only needs a plain JDK (no JavaFX); resolve one below.

Write-Host ""
Write-Host "Building bs-engine-patch tool..." -ForegroundColor Cyan

# Resolve a JDK: the in-repo Liberica if the Java-agent block installed it,
# else JAVA_HOME (CI's offline lanes provision one via actions/setup-java). No JavaFX needed.
$patchJdk = if (Test-Path (Join-Path $repoRoot 'lib/liberica-jdk/bin')) {
    Join-Path $repoRoot 'lib/liberica-jdk'
} elseif ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin'))) {
    $env:JAVA_HOME
} else {
    $null
}
if (-not $patchJdk) {
    throw "bs-engine-patch needs a JDK 11+ to compile: neither lib/liberica-jdk nor JAVA_HOME is available. In CI, add an actions/setup-java step before setup.ps1 on any lane that builds the .NET engine."
}
$patchBuild = Join-Path $repoRoot 'src/bs-engine-patch/build.ps1'
pwsh -File $patchBuild -JavaHome $patchJdk
if ($LASTEXITCODE -ne 0) { throw "bs-engine-patch build failed" }
Write-Host "[OK] bs-engine-patch tool built" -ForegroundColor Green

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
