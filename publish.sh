#!/usr/bin/env bash
set -e

PUBLISH_DIR="./publish"
STAGING_DIR="./publish_temp"

echo "=================================================================="
echo " 📦 ASQA IIS PUBLISH WIZARD"
echo "=================================================================="

MODE="$1"
if [ "$MODE" != "1" ] && [ "$MODE" != "2" ]; then
    echo "Please select your deployment mode:"
    echo "  [1] Full Self-Contained Deployment (First-Time Setup, ~110MB)"
    echo "  [2] Fast Delta Deploy (App & UI Updates Only, ~15MB)"
    read -p "Enter choice [1 or 2]: " MODE
fi

rm -rf "$PUBLISH_DIR" "$STAGING_DIR"
mkdir -p "$PUBLISH_DIR"

package_agent() {
    echo "📦 Packaging ASQA Client Agents..."
    local AGENT_STAGING="$STAGING_DIR/agent"
    local RUNTIME_CACHE="ASQATools/App_Data/runtime_cache"
    local CACHE_BIN="$RUNTIME_CACHE/win-x64-binaries"
    mkdir -p "$AGENT_STAGING" "$PUBLISH_DIR/wwwroot/downloads" "ASQATools/wwwroot/downloads" "$RUNTIME_CACHE"
    dotnet publish ASQATools.LocalBridge/ASQATools.LocalBridge.csproj -c Release -o "$AGENT_STAGING"
    cp -f ASQATools.LocalBridge/start-agent.sh "$AGENT_STAGING/"
    chmod +x "$AGENT_STAGING/start-agent.sh" 2>/dev/null || true

    # 1. Package Lightweight ZIP
    if command -v zip &> /dev/null; then
        (cd "$AGENT_STAGING" && zip -r -q "$OLDPWD/$PUBLISH_DIR/wwwroot/downloads/ASQA_LocalAgent.zip" .)
    else
        powershell.exe -NoProfile -Command "Compress-Archive -Path '$AGENT_STAGING\*' -DestinationPath '$PUBLISH_DIR/wwwroot/downloads/ASQA_LocalAgent.zip' -Force"
    fi

    # 2. Check for or harvest portable runtime for Full Bundle fallback (1-time)
    if [ ! -f "$CACHE_BIN/dotnet.exe" ]; then
        local LOCAL_DOTNET="/c/Program Files/dotnet"
        if [ ! -d "$LOCAL_DOTNET" ]; then LOCAL_DOTNET="C:/Program Files/dotnet"; fi
        if [ -f "$LOCAL_DOTNET/dotnet.exe" ] && [ -d "$LOCAL_DOTNET/shared/Microsoft.AspNetCore.App" ]; then
            echo "⚡ Harvesting .NET runtime from local machine: $LOCAL_DOTNET..."
            mkdir -p "$CACHE_BIN/shared" "$CACHE_BIN/host"
            cp -f "$LOCAL_DOTNET/dotnet.exe" "$CACHE_BIN/"
            cp -rf "$LOCAL_DOTNET/host" "$CACHE_BIN/" 2>/dev/null || true
            cp -rf "$LOCAL_DOTNET/shared/Microsoft.NETCore.App" "$CACHE_BIN/shared/" 2>/dev/null || true
            cp -rf "$LOCAL_DOTNET/shared/Microsoft.AspNetCore.App" "$CACHE_BIN/shared/" 2>/dev/null || true
        fi
    fi

    # 3. Package Full Self-Contained Bundle if runtime is cached
    if [ -f "$CACHE_BIN/dotnet.exe" ]; then
        echo "🛡️ Packaging Full Standalone Bundle with bundled runtime..."
        mkdir -p "$AGENT_STAGING/runtime"
        cp -rf "$CACHE_BIN/"* "$AGENT_STAGING/runtime/"
        if command -v zip &> /dev/null; then
            (cd "$AGENT_STAGING" && zip -r -q "$OLDPWD/$PUBLISH_DIR/wwwroot/downloads/ASQA_LocalAgent_Full.zip" .)
        else
            powershell.exe -NoProfile -Command "Compress-Archive -Path '$AGENT_STAGING\*' -DestinationPath '$PUBLISH_DIR/wwwroot/downloads/ASQA_LocalAgent_Full.zip' -Force"
        fi
        rm -rf "$AGENT_STAGING/runtime"
    fi
    cp -f "$PUBLISH_DIR/wwwroot/downloads/ASQA_LocalAgent"*.zip "ASQATools/wwwroot/downloads/" 2>/dev/null || true
}

if [ "$MODE" == "1" ]; then
    echo "🚀 Building [Option 1: Full Self-Contained Deployment]..."
    dotnet publish ASQATools/ASQATools.csproj -c Release -r win-x64 --self-contained true -o "$PUBLISH_DIR"
    rm -f "$PUBLISH_DIR"/createdump.exe

    cat << 'WCFG' > "$PUBLISH_DIR/web.config"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <clear />
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath=".\ASQATools.exe" arguments="" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
WCFG

    mkdir -p "$PUBLISH_DIR/Templates"
    cp -rf ASQATools/Templates/* "$PUBLISH_DIR/Templates/"
    rm -rf "$PUBLISH_DIR/pr_reviews_data"

    package_agent
    rm -rf "$STAGING_DIR"

elif [ "$MODE" == "2" ]; then
    echo "⚡ Building [Option 2: Fast Delta Deploy (App & UI Only)]..."
    dotnet publish ASQATools/ASQATools.csproj -c Release -r win-x64 --self-contained true -o "$STAGING_DIR"

    cp -f "$STAGING_DIR"/ASQATools.dll "$PUBLISH_DIR/"
    cp -f "$STAGING_DIR"/ASQATools.Shared.dll "$PUBLISH_DIR/"
    cp -f "$STAGING_DIR"/ASQATools.Client.dll "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.Shared.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.Client.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.deps.json "$PUBLISH_DIR/"
    cp -rf "$STAGING_DIR"/wwwroot "$PUBLISH_DIR/"

    package_agent
    mkdir -p "$PUBLISH_DIR/Templates"
    cp -rf ASQATools/Templates/* "$PUBLISH_DIR/Templates/"
    rm -rf "$STAGING_DIR"
fi

echo "=================================================================="
echo " ✔ Publish complete in: $(pwd)/publish"
echo " (Your project files remain configured for development without changes)"
echo "=================================================================="
