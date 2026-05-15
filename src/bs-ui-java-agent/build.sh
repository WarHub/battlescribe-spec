#!/usr/bin/env bash
# Build the bs-ui-java-agent.jar
# Usage: ./build.sh [path-to-javac] [path-to-jar]
# Requires JDK 11+ with JavaFX modules (e.g. Liberica Full JDK).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
OUT_DIR="$SCRIPT_DIR/out"
JAR_NAME="bs-ui-java-agent.jar"

JAVAC="${1:-javac}"
JAR_CMD="${2:-jar}"

echo "[bs-ui-java-agent] Compiling..."
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/classes"

# Find all .java files
find "$SRC_DIR" -name '*.java' > "$OUT_DIR/sources.txt"

"$JAVAC" \
    --add-modules javafx.controls \
    -d "$OUT_DIR/classes" \
    @"$OUT_DIR/sources.txt"

echo "[bs-ui-java-agent] Packaging $JAR_NAME..."
"$JAR_CMD" cfm "$SCRIPT_DIR/$JAR_NAME" "$SCRIPT_DIR/MANIFEST.MF" \
    -C "$OUT_DIR/classes" .

echo "[bs-ui-java-agent] Built: $SCRIPT_DIR/$JAR_NAME"
