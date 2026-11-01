#!/usr/bin/env bash
# Source this file to keep every .NET / NuGet / Python cache inside the checkout.
# Usage: source scripts/env.sh
BYDLO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export BYDLO_ROOT
export TMP="$BYDLO_ROOT/.tmp/tmp"
export TEMP="$TMP"
export TMPDIR="$TMP"
export DOTNET_CLI_HOME="$BYDLO_ROOT/.tmp/dotnet-home"
export NUGET_PACKAGES="$BYDLO_ROOT/.tmp/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$BYDLO_ROOT/.tmp/nuget/http-cache"
export NUGET_PLUGINS_CACHE_PATH="$BYDLO_ROOT/.tmp/nuget/plugins-cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export PIP_CACHE_DIR="$BYDLO_ROOT/.tmp/pip-cache"
export PYTHONPYCACHEPREFIX="$BYDLO_ROOT/.tmp/pycache"
# Development safety lock: real network engines refuse to start while this is set.
# App data of local debug runs stays inside the checkout too.
export BYDLO_DATA_DIR="$BYDLO_ROOT/.tmp/appdata"
export BYDLO_SAFE_DEV=1
mkdir -p "$TMP" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$NUGET_PLUGINS_CACHE_PATH"
