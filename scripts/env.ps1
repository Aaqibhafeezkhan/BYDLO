# Dot-source this file to keep every .NET / NuGet / Python cache inside the checkout.
# Usage: . ./scripts/env.ps1
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:BYDLO_ROOT = $root
$env:TMP = Join-Path $root '.tmp/tmp'
$env:TEMP = $env:TMP
$env:TMPDIR = $env:TMP
$env:DOTNET_CLI_HOME = Join-Path $root '.tmp/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tmp/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root '.tmp/nuget/http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $root '.tmp/nuget/plugins-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:PIP_CACHE_DIR = Join-Path $root '.tmp/pip-cache'
$env:PYTHONPYCACHEPREFIX = Join-Path $root '.tmp/pycache'
# Development safety lock: real network engines refuse to start while this is set.
# App data of local debug runs stays inside the checkout too.
$env:BYDLO_DATA_DIR = Join-Path $root '.tmp/appdata'
$env:BYDLO_SAFE_DEV = '1'
foreach ($d in @($env:TMP, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH)) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}
