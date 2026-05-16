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

& $javac --add-modules javafx.controls,javafx.swing -d (Join-Path $OutDir 'classes') @sources
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
