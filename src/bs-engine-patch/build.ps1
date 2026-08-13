# Compile the bs-engine-patch tool classes (ErrorIdPatcher / PatchJarMain / ErrorIdTransformer)
# against the vendored ASM jar. Output lands in out/classes and is consumed by:
#   - the PatchBattleScribeEngineJar MSBuild target (in-process lane: runs `java PatchJarMain`), and
#   - src/bs-ui-java-agent/build.ps1 (UI lane: recompiles the same sources INTO the agent jar with
#     ASM shaded in; this module's out/ is not used there).
# Usage: .\build.ps1 [-JavaHome <path>]
# Requires JDK 11+ (no JavaFX). setup.ps1 vendors ASM to lib/asm/ before calling this.

param(
    [string]$JavaHome
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$SrcDir = Join-Path $ScriptDir 'src'
$OutDir = Join-Path $ScriptDir 'out/classes'

# Locate the vendored ASM jar (setup.ps1 puts it here with a checksum check).
$AsmDir = Join-Path $ScriptDir '../../lib/asm'
$AsmJar = if (Test-Path $AsmDir) {
    Get-ChildItem $AsmDir -Filter 'asm-*.jar' | Select-Object -First 1 -ExpandProperty FullName
} else { $null }
if (-not $AsmJar) {
    throw "ASM jar not found under lib/asm — run setup.ps1 first (it vendors ASM with a checksum)."
}

# JDK discovery mirrors src/bs-ui-java-agent/build.ps1: explicit param, then JAVA_HOME, then the
# in-repo Liberica JDK that setup.ps1 installs for local dev.
if ($JavaHome) {
    # use as-is
} elseif ($env:JAVA_HOME) {
    $JavaHome = $env:JAVA_HOME
} else {
    $searchDir = $ScriptDir
    while ($searchDir) {
        $candidate = Join-Path $searchDir 'lib' 'liberica-jdk'
        if (Test-Path (Join-Path $candidate 'bin')) { $JavaHome = $candidate; break }
        $parent = Split-Path $searchDir -Parent
        if ($parent -eq $searchDir) { break }
        $searchDir = $parent
    }
}

if ($JavaHome) {
    if ($IsLinux -or $IsMacOS) { $javac = Join-Path $JavaHome 'bin/javac' }
    else { $javac = Join-Path $JavaHome 'bin\javac.exe' }
} else {
    $javac = 'javac'
}

Write-Host '[bs-engine-patch] Compiling patch tool...'
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$sources = Get-ChildItem -Path $SrcDir -Filter '*.java' -Recurse | ForEach-Object { $_.FullName }
# --release 11: the PatchBattleScribeEngineJar MSBuild target runs these classes with WHATEVER JDK
# it resolves (JAVA_HOME or lib/liberica-jdk, a Java 11 in CI), which need not be the JDK that
# compiled them here. Targeting Java 11 bytecode keeps the tool loadable regardless of the compiler
# -- otherwise a newer compile JDK (e.g. the runner default) yields a class version the runtime JDK
# rejects. ASM 9.7 and the tool use only Java 11-compatible APIs.
& $javac --release 11 -encoding UTF-8 -classpath $AsmJar -d $OutDir @sources
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }

Write-Host "[bs-engine-patch] Built classes in $OutDir (ASM: $(Split-Path $AsmJar -Leaf))"
