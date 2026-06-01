# Build the bs-ui-java-agent.jar
# Usage: .\build.ps1 [-JavaHome <path>]
# Requires JDK 11+ with JavaFX modules (e.g. Liberica Full JDK).

param(
    [string]$JavaHome
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$SrcDir = Join-Path $ScriptDir 'src'
$OutDir = Join-Path $ScriptDir 'out'
$JarName = 'bs-ui-java-agent.jar'
# Compile against Gson; BattleScribe already provides it on the app runtime classpath.
$GsonJar = Join-Path $ScriptDir '../../lib/battlescribe/lib/gson-2.1.jar'

if (!(Test-Path $GsonJar)) {
    throw "gson dependency not found: $GsonJar"
}

if ($JavaHome) {
    # Explicit parameter — use as-is
} elseif ($env:JAVA_HOME) {
    # Respect JAVA_HOME environment variable (set by CI via actions/setup-java)
    $JavaHome = $env:JAVA_HOME
} else {
    # Auto-discover in-repo Liberica JDK (installed by setup.ps1 for local dev)
    $searchDir = $ScriptDir
    while ($searchDir) {
        $candidate = Join-Path $searchDir 'lib' 'liberica-jdk'
        if (Test-Path $candidate) {
            $jdkSubdir = if (Test-Path (Join-Path $candidate 'bin')) {
                $candidate  # tar --strip-components case (Linux/macOS)
            } else {
                (Get-ChildItem $candidate -Directory -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path (Join-Path $_.FullName 'bin') } |
                    Select-Object -First 1)?.FullName
            }
            if ($jdkSubdir) { $JavaHome = $jdkSubdir; break }
        }
        $parent = Split-Path $searchDir -Parent
        if ($parent -eq $searchDir) { break }  # filesystem root
        $searchDir = $parent
    }
}

if ($JavaHome) {
    if ($IsLinux -or $IsMacOS) {
        $javac = Join-Path $JavaHome 'bin/javac'
        $jar = Join-Path $JavaHome 'bin/jar'
    } else {
        $javac = Join-Path $JavaHome 'bin\javac.exe'
        $jar = Join-Path $JavaHome 'bin\jar.exe'
    }
} else {
    $javac = 'javac'
    $jar = 'jar'
}

Write-Host '[bs-ui-java-agent] Compiling...'
if (Test-Path $OutDir) {
    Remove-Item $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $OutDir 'classes') -Force | Out-Null

$sources = Get-ChildItem -Path $SrcDir -Filter '*.java' -Recurse | ForEach-Object { $_.FullName }

& $javac --add-modules javafx.controls -classpath $GsonJar -d (Join-Path $OutDir 'classes') @sources
if ($LASTEXITCODE -ne 0) {
    throw 'javac failed'
}

Write-Host "[bs-ui-java-agent] Packaging $JarName..."
$jarPath = Join-Path $ScriptDir $JarName
$manifestPath = Join-Path $ScriptDir 'MANIFEST.MF'
$classesDir = Join-Path $OutDir 'classes'

& $jar cfm $jarPath $manifestPath -C $classesDir .
if ($LASTEXITCODE -ne 0) {
    throw 'jar failed'
}

Write-Host "[bs-ui-java-agent] Built: $jarPath"
