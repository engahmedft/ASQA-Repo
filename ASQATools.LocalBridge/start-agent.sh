#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "============================================================"
echo " 🚀 ASQA Local Desktop Agent Launcher"
echo "============================================================"

# Test runner execution to detect AppLocker/EDR policy blocks
can_run() {
    local candidate="$1"
    if [ -x "$candidate" ] || [ -f "$candidate" ] || command -v "$candidate" &>/dev/null; then
        if "$candidate" --version &>/dev/null; then
            return 0
        fi
    fi
    return 1
}

RUNNER=""

# 1. Strategy 1: Check bundled portable runtime
if [ -f "./runtime/dotnet.exe" ] && can_run "./runtime/dotnet.exe"; then
    echo "✔ Strategy 1: Using bundled portable runtime (./runtime/dotnet.exe)."
    RUNNER="./runtime/dotnet.exe"
elif [ -f "./runtime/dotnet.exe" ]; then
    echo "⚠️ Strategy 1 Warning: ./runtime/dotnet.exe is blocked by system policy."
    echo "   Attempting automatic fallback to whitelisted system runtime..."
fi

# 2. Strategy 2: Fallback to whitelisted system paths
if [ -z "$RUNNER" ]; then
    if can_run "/c/Program Files/dotnet/dotnet.exe"; then
        echo "✔ Strategy 2 Fallback: Using system runtime (/c/Program Files/dotnet/dotnet.exe)."
        RUNNER="/c/Program Files/dotnet/dotnet.exe"
    elif can_run "C:/Program Files/dotnet/dotnet.exe"; then
        echo "✔ Strategy 2 Fallback: Using system runtime (C:/Program Files/dotnet/dotnet.exe)."
        RUNNER="C:/Program Files/dotnet/dotnet.exe"
    elif can_run "dotnet"; then
        echo "✔ Strategy 2 Fallback: Using system 'dotnet' from PATH."
        RUNNER="dotnet"
    fi
fi

if [ -z "$RUNNER" ]; then
    echo "❌ Error: No executable .NET runtime was found or permitted on this machine."
    read -p "Press Enter to exit..."
    exit 1
fi

# Locate the target DLL
TARGET_DLL="ASQATools.LocalBridge.dll"
if [ ! -f "$TARGET_DLL" ]; then
    if [ -f "bin/Release/net10.0/ASQATools.LocalBridge.dll" ]; then
        TARGET_DLL="bin/Release/net10.0/ASQATools.LocalBridge.dll"
    elif [ -f "bin/Debug/net10.0/ASQATools.LocalBridge.dll" ]; then
        TARGET_DLL="bin/Debug/net10.0/ASQATools.LocalBridge.dll"
    fi
fi

echo "Starting agent: $RUNNER $TARGET_DLL"
"$RUNNER" "$TARGET_DLL"
