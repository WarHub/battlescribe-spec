#!/usr/bin/env bash
# Build the bs-ui-java-agent.jar
# Usage: ./build.sh [path-to-javac] [path-to-jar]
# Requires JDK 11+ with JavaFX modules (e.g. Liberica Full JDK).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
OUT_DIR="$SCRIPT_DIR/out"
JAR_NAME="bs-ui-java-agent.jar"
# Compile against Gson; BattleScribe already provides it on the app runtime classpath.
GSON_JAR="$SCRIPT_DIR/../../lib/battlescribe/lib/gson-2.1.jar"

if [[ ! -f "$GSON_JAR" ]]; then
    echo "gson dependency not found: $GSON_JAR" >&2
    exit 1
fi

JAVAC=""
JAR_CMD=""

if [ -n "${1:-}" ]; then
    # Explicit positional args
    JAVAC="$1/bin/javac"
    JAR_CMD="${2:-$1/bin/jar}"
elif [ -n "${JAVA_HOME:-}" ]; then
    # Respect JAVA_HOME environment variable (set by CI via actions/setup-java)
    JAVAC="$JAVA_HOME/bin/javac"
    JAR_CMD="$JAVA_HOME/bin/jar"
else
    # Auto-discover in-repo Liberica JDK (installed by setup.ps1 for local dev)
    SEARCH_DIR="$SCRIPT_DIR"
    while [ "$SEARCH_DIR" != "/" ] && [ -n "$SEARCH_DIR" ]; do
        CANDIDATE="$SEARCH_DIR/lib/liberica-jdk"
        if [ -d "$CANDIDATE/bin" ]; then
            JAVAC="$CANDIDATE/bin/javac"
            JAR_CMD="$CANDIDATE/bin/jar"
            break
        elif [ -d "$CANDIDATE" ]; then
            for subdir in "$CANDIDATE"/*/; do
                if [ -f "$subdir/bin/javac" ]; then
                    JAVAC="$subdir/bin/javac"
                    JAR_CMD="$subdir/bin/jar"
                    break
                fi
            done
            [ -n "$JAVAC" ] && break
        fi
        SEARCH_DIR="$(dirname "$SEARCH_DIR")"
    done
    if [ -z "$JAVAC" ]; then
        JAVAC="javac"
        JAR_CMD="jar"
    fi
fi

echo "[bs-ui-java-agent] Compiling..."
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/classes"

# Find all .java files
find "$SRC_DIR" -name '*.java' > "$OUT_DIR/sources.txt"

"$JAVAC" \
    --add-modules javafx.controls \
    -classpath "$GSON_JAR" \
    -d "$OUT_DIR/classes" \
    @"$OUT_DIR/sources.txt"

echo "[bs-ui-java-agent] Packaging $JAR_NAME..."
"$JAR_CMD" cfm "$SCRIPT_DIR/$JAR_NAME" "$SCRIPT_DIR/MANIFEST.MF" \
    -C "$OUT_DIR/classes" .

echo "[bs-ui-java-agent] Built: $SCRIPT_DIR/$JAR_NAME"
