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

# The agent registers the SAME engine-jar transform the in-process build applies, from its own
# premain (no second -javaagent). It shares the source with src/bs-engine-patch and needs ASM at
# RUNTIME inside the BattleScribe JVM, so ASM is compiled against and shaded into the agent jar.
$EnginePatchSrc = Join-Path $ScriptDir '../bs-engine-patch/src'
$AsmDir = Join-Path $ScriptDir '../../lib/asm'
$AsmJar = if (Test-Path $AsmDir) {
    Get-ChildItem $AsmDir -Filter 'asm-*.jar' | Select-Object -First 1 -ExpandProperty FullName
} else { $null }
if (!(Test-Path $EnginePatchSrc)) {
    throw "bs-engine-patch sources not found: $EnginePatchSrc"
}
if (-not $AsmJar) {
    throw "ASM jar not found under lib/asm — run setup.ps1 first (it vendors ASM with a checksum)."
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
        # setup.ps1 normalizes the in-repo JDK so bin/ is directly under lib/liberica-jdk on every OS.
        $candidate = Join-Path $searchDir 'lib' 'liberica-jdk'
        if (Test-Path (Join-Path $candidate 'bin')) { $JavaHome = $candidate; break }
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

$sources = Get-ChildItem -Path $SrcDir, $EnginePatchSrc -Filter '*.java' -Recurse | ForEach-Object { $_.FullName }

# -encoding UTF-8 is not optional: the sources are UTF-8 (box-drawing characters in comments) and
# javac otherwise decodes them with the platform default charset, which on a Windows machine is
# typically windows-1252 — 100 "unmappable character" errors and no jar. CI runs on Linux, where the
# default already is UTF-8, so the flag's absence only ever broke local Windows setup.
$classesDir = Join-Path $OutDir 'classes'
& $javac -encoding UTF-8 --add-modules javafx.controls -classpath "$GsonJar$([System.IO.Path]::PathSeparator)$AsmJar" -d $classesDir @sources
if ($LASTEXITCODE -ne 0) {
    throw 'javac failed'
}

# Shade ASM into the classes dir so the transformer can run inside the app JVM without ASM on the
# classpath. Extract asm-*.jar's org/objectweb/asm/** over our classes (drop its META-INF).
Write-Host '[bs-ui-java-agent] Shading ASM...'
$asmExtract = Join-Path $OutDir 'asm-extract'
if (Test-Path $asmExtract) { Remove-Item $asmExtract -Recurse -Force }
New-Item -ItemType Directory -Path $asmExtract -Force | Out-Null
Push-Location $asmExtract
try {
    & $jar xf $AsmJar
    if ($LASTEXITCODE -ne 0) { throw 'ASM extract failed' }
} finally {
    Pop-Location
}
Copy-Item (Join-Path $asmExtract 'org') $classesDir -Recurse -Force
if (-not (Test-Path (Join-Path $classesDir 'org/objectweb/asm/ClassReader.class'))) {
    throw 'ASM shading failed: org/objectweb/asm not present in classes'
}

Write-Host "[bs-ui-java-agent] Packaging $JarName..."
$jarPath = Join-Path $ScriptDir $JarName
$manifestPath = Join-Path $ScriptDir 'MANIFEST.MF'

& $jar cfm $jarPath $manifestPath -C $classesDir .
if ($LASTEXITCODE -ne 0) {
    throw 'jar failed'
}

Write-Host "[bs-ui-java-agent] Built: $jarPath"
