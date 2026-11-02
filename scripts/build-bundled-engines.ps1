param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$repo = $env:BYDLO_ROOT
$output = [IO.Path]::GetFullPath($PublishDirectory)
$stage = Join-Path $repo ('.tmp/native-build-' + $Rid)
New-Item -ItemType Directory -Force $stage | Out-Null
$inputs = Get-Content (Join-Path $repo 'engines/bundle-inputs.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$engines = (Get-Content (Join-Path $repo 'engines/engines.lock.json') -Raw | ConvertFrom-Json).engines
$sources = Join-Path $output 'engines/sources'
New-Item -ItemType Directory -Force $sources | Out-Null

function Fetch([string]$Id) {
    $entry = $inputs[$Id]
    if (!$entry) { throw "Unknown build input $Id" }
    $file = Join-Path $stage ([IO.Path]::GetFileName(([uri]$entry.url).AbsolutePath))
    if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        Invoke-WebRequest $entry.url -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "SHA-256 mismatch: $Id" }
    return $file
}
function CheckExit([string]$Action) { if ($LASTEXITCODE -ne 0) { throw "$Action failed: $LASTEXITCODE" } }

foreach ($sourceId in @('sing-box-source','xray-source','openssl-source')) {
    Copy-Item (Fetch $sourceId) (Join-Path $sources ($sourceId + '.tar.gz')) -Force
}

# These are build tools, not installed globally. CI supplies the pinned Go toolchain.
$env:GOPATH = Join-Path $repo '.tmp/go-path'
$env:GOCACHE = Join-Path $repo '.tmp/go-cache'
$env:GOTOOLCHAIN = 'local'
$env:CGO_ENABLED = '0'
$env:GOOS = if ($Rid.StartsWith('win-')) { 'windows' } elseif ($Rid.StartsWith('linux-')) { 'linux' } else { 'darwin' }
$env:GOARCH = if ($Rid.EndsWith('arm64')) { 'arm64' } else { 'amd64' }
if (!$Rid.StartsWith('osx-')) {
    $awg = $engines | Where-Object id -eq 'amneziawg'
    $awgDir = Join-Path $output "engines/amneziawg/$($awg.version)"
    New-Item -ItemType Directory -Force $awgDir | Out-Null
    $binary = if ($env:GOOS -eq 'windows') { 'bydlo-amneziawg.exe' } else { 'bydlo-amneziawg' }
    & go -C (Join-Path $repo 'tools/amneziawg') mod verify
    CheckExit 'Go module verification'
    & go -C (Join-Path $repo 'tools/amneziawg') build -mod=readonly -trimpath -buildvcs=false -ldflags=-s -o (Join-Path $awgDir $binary) .
    CheckExit 'AmneziaWG build'
    $sourceDir = Join-Path $stage 'amneziawg-source'
    New-Item -ItemType Directory -Force $sourceDir | Out-Null
    Copy-Item (Join-Path $repo 'tools/amneziawg/*') $sourceDir -Exclude 'vendor' -Recurse -Force
    Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $sourceDir 'LICENSE-BYDLO') -Force
    & go -C (Join-Path $repo 'tools/amneziawg') mod vendor -o (Join-Path $sourceDir 'vendor')
    CheckExit 'AmneziaWG corresponding source'
    & tar -czf (Join-Path $sources 'amneziawg-with-dependencies.tar.gz') -C $sourceDir .
    CheckExit 'AmneziaWG source archive'
    if ($env:GOOS -eq 'windows') {
        $wintun = Fetch 'wintun'
        Expand-Archive -LiteralPath $wintun -DestinationPath (Join-Path $stage 'wintun') -Force
        Copy-Item (Join-Path $stage "wintun/wintun/bin/$($env:GOARCH)/wintun.dll") $awgDir -Force
    }
}

$ovpn = $engines | Where-Object id -eq 'openvpn'
$ovpnDir = Join-Path $output "engines/openvpn/$($ovpn.version)"
New-Item -ItemType Directory -Force $ovpnDir | Out-Null
$openvpnSource = Fetch 'openvpn-source'
Copy-Item $openvpnSource $sources -Force
if ($Rid.StartsWith('win-')) {
    Copy-Item (Fetch 'dco-source') (Join-Path $sources 'ovpn-dco-2.8.14-source.tar.gz') -Force
    $msi = Fetch ('openvpn-windows-' + $Rid.Split('-')[1])
    $lessmsi = Fetch 'lessmsi'
    Expand-Archive -LiteralPath $lessmsi -DestinationPath (Join-Path $stage 'lessmsi') -Force
    # Extract only. Never execute MSI custom actions, GUI, engine, driver or service.
    $extract = (Join-Path $stage 'openvpn-extracted') + [IO.Path]::DirectorySeparatorChar
    & (Join-Path $stage 'lessmsi/lessmsi.exe') x $msi $extract
    CheckExit 'Read-only MSI extraction'
    $original = Join-Path $extract 'SourceDir/OpenVPN'
    foreach ($name in @('openvpn.exe','tapctl.exe','vcruntime140.dll','libpkcs11-helper-1.dll')) {
        Copy-Item (Join-Path $original "bin/$name") $ovpnDir -Force
    }
    Copy-Item (Join-Path $original 'bin/libssl-3-*.dll'),(Join-Path $original 'bin/libcrypto-3-*.dll') $ovpnDir -Force
    Copy-Item (Join-Path $original 'Common Files/ovpn-dco') (Join-Path $ovpnDir 'dco') -Recurse -Force
    Copy-Item (Join-Path $original 'license.txt') $ovpnDir -Force
} else {
    $opensslSource = Fetch 'openssl-source'
    $capSource = ''
    if ($Rid.StartsWith('linux-')) {
        $capSource = Fetch 'libcap-ng-source'
        Copy-Item $capSource (Join-Path $sources 'libcap-ng-source.tar.gz') -Force
    }
    & bash (Join-Path $repo 'scripts/build-openvpn.sh') $Rid $stage $ovpnDir $openvpnSource $opensslSource $capSource
    CheckExit 'OpenVPN/OpenSSL build'
}

# Record packaged bytes without launching them. Retain build inputs for audit/update work.
Copy-Item (Join-Path $repo 'engines/bundle-inputs.lock.json') (Join-Path $output 'engines') -Force
Copy-Item (Join-Path $repo 'scripts/build-openvpn.sh') $sources -Force
Get-ChildItem (Join-Path $output 'engines') -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), ([IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/'))
} | Set-Content (Join-Path $output 'ENGINES-SHA256SUMS.txt') -Encoding utf8NoBOM
